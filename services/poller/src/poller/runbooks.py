"""The agent half of auto-remediation: a closed registry of things this poller knows how to do.

The platform never sends a command. It sends a *key* and parameters it has already validated against
that runbook's schema, and this module decides what that key means. That is the second half of
ARCHITECTURE §7 invariant 4 — the allowlist is enforced at both ends, so a compromised database, a
forged config or a restored backup cannot make this process run something new. A key that is not in
`RUNBOOKS` is refused, loudly, and reported as a failure the platform escalates.

Nothing here builds a shell command line. Every implementation runs `create_subprocess_exec` with an
argv list assembled from a template held in this process's settings, so there is no string a
parameter could be interpolated into and no shell to interpret one if there were.
"""

from __future__ import annotations

import asyncio
import logging
import re
import shlex
from collections.abc import Awaitable, Callable, Mapping
from dataclasses import dataclass
from typing import Any

from .settings import Settings

logger = logging.getLogger("poller.runbooks")

#: How much of a runbook's output is kept per stream. The platform truncates again on the way in;
#: this bound exists so a runaway process cannot make the poller hold a gigabyte of stdout.
MAX_OUTPUT_CHARACTERS = 8_000

SUCCEEDED = "Succeeded"
FAILED = "Failed"
TIMED_OUT = "TimedOut"

#: What a substituted parameter may look like once it reaches this side. The server validated it
#: against the same shape before storing it; this is the second, independent check — the one that
#: still holds if the server is wrong, which is the only kind of check worth having on an agent.
SAFE_ARGUMENT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._@:/-]{0,127}$")


class UnsafeParameterError(RuntimeError):
    """A parameter did not survive this agent's own check. Nothing is run."""


@dataclass(frozen=True, slots=True)
class RunbookRequest:
    """One execution, as the platform handed it over."""

    execution_id: str
    key: str
    version: int
    parameters: Mapping[str, str]
    timeout_seconds: int
    device_id: str
    address: str

    @classmethod
    def parse(cls, payload: Mapping[str, Any]) -> RunbookRequest:
        raw = payload.get("parameters") or {}
        return cls(
            execution_id=str(payload["executionId"]),
            key=str(payload["runbookKey"]),
            version=int(payload.get("runbookVersion") or 0),
            parameters={str(name): str(value) for name, value in raw.items()},
            timeout_seconds=int(payload.get("timeoutSeconds") or 60),
            device_id=str(payload.get("deviceId") or ""),
            address=str(payload.get("address") or ""),
        )


@dataclass(frozen=True, slots=True)
class RunbookResult:
    """What is reported back. The three outcomes are the ones the platform accepts."""

    execution_id: str
    outcome: str
    exit_code: int | None = None
    output: str | None = None
    error: str | None = None

    def as_payload(self) -> dict[str, Any]:
        return {
            "outcome": self.outcome,
            "exitCode": self.exit_code,
            "output": self.output,
            "error": self.error,
        }


#: An implementation: given the request and the settings, produce a result.
RunbookHandler = Callable[[RunbookRequest, Settings], Awaitable[RunbookResult]]


def _truncate(value: str) -> str:
    stripped = value.strip()
    if len(stripped) <= MAX_OUTPUT_CHARACTERS:
        return stripped
    return stripped[:MAX_OUTPUT_CHARACTERS] + "\n… truncated by the poller."


def _safe(value: str) -> str:
    """
    The agent's own parameter check, deliberately duplicating the server's.

    It is not defence in depth for its own sake: this is the last point before a value becomes an
    argv element, and it is the only check that still runs if the row it came from was written by
    something other than the API.
    """
    if not SAFE_ARGUMENT.match(value):
        raise UnsafeParameterError(
            "A runbook parameter did not match the shape this agent accepts; nothing was run."
        )
    return value


def build_argv(template: str, **substitutions: str) -> list[str]:
    """
    Turn a configured template into an argv list.

    The template is split into words *first*, with `shlex.split`, and the placeholders are then
    replaced inside the resulting elements. That ordering is the whole safety property: a parameter
    containing a space, a semicolon or a quote becomes one argv element containing those characters,
    never a second word and never a second command. There is no shell anywhere in this path.
    """
    words = shlex.split(template)
    if not words:
        raise ValueError("A runbook command template cannot be empty.")

    argv: list[str] = []
    for word in words:
        rendered = word
        for name, value in substitutions.items():
            rendered = rendered.replace("{" + name + "}", _safe(value))
        argv.append(rendered)
    return argv


async def _run_process(
    request: RunbookRequest,
    argv: list[str],
) -> RunbookResult:
    """
    Run one argv list with its own timeout, and never raise.

    A runbook is remediation, so its failure is a result rather than an exception — the platform has
    to be told, and an exception here would leave the execution to be timed out by the sweeper
    minutes later instead.
    """
    logger.info(
        "Running runbook.",
        extra={
            "execution": request.execution_id,
            "runbook": request.key,
            "device": request.device_id,
            # The argv, because a runbook is an action on a machine and "what exactly did it run"
            # is the first question anybody asks afterwards. It carries no secret: a runbook is
            # never given credential material.
            "argv": argv,
        },
    )
    try:
        process = await asyncio.create_subprocess_exec(
            *argv,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
    except (OSError, ValueError) as error:
        return RunbookResult(
            request.execution_id,
            FAILED,
            error=f"The runbook could not be started: {error}",
        )

    try:
        stdout, stderr = await asyncio.wait_for(
            process.communicate(), timeout=request.timeout_seconds
        )
    except TimeoutError:
        # Killed rather than left running. The platform will time this execution out on its own
        # clock too, but a process the agent has stopped waiting for and has not stopped is one
        # nothing will ever account for.
        process.kill()
        await process.wait()
        return RunbookResult(
            request.execution_id,
            TIMED_OUT,
            error=f"The runbook did not finish within {request.timeout_seconds}s and was killed.",
        )

    out = _truncate(stdout.decode("utf-8", errors="replace"))
    err = _truncate(stderr.decode("utf-8", errors="replace"))
    code = process.returncode
    if code == 0:
        return RunbookResult(
            request.execution_id, SUCCEEDED, exit_code=code, output=out, error=err or None
        )
    return RunbookResult(
        request.execution_id,
        FAILED,
        exit_code=code,
        output=out,
        error=err or f"The runbook exited with status {code}.",
    )


async def restart_service(request: RunbookRequest, settings: Settings) -> RunbookResult:
    """
    Restart one named service on the host this poller runs on.

    The command is a template this process was configured with — `systemctl restart {service}` by
    default — and the only thing that varies is the unit name, which the server validated against
    the runbook's schema and `build_argv` validates again here.
    """
    service = request.parameters.get("service", "")
    if not service:
        return RunbookResult(
            request.execution_id, FAILED, error="No service was named; nothing was restarted."
        )

    argv = build_argv(settings.runbook_restart_service_command, service=service)
    return await _run_process(request, argv)


#: The allowlist, as this agent holds it. Adding a runbook is a code change here and a catalogue
#: entry on the server, and the two are checked against each other at run time rather than at build
#: time: a key one side knows and the other does not is refused below and escalates like a failure.
RUNBOOKS: Mapping[str, RunbookHandler] = {
    "restart-service": restart_service,
}


class RunbookRunner:
    """
    Runs what the platform handed over, one at a time, and never raises.

    Sequential on purpose. Polling is concurrent because it is measurement and a slow device must
    not hold up an estate; remediation is not measurement. Two remediations at once on one host is a
    combination nobody has reasoned about, and the batch the platform hands over is small by
    configuration.
    """

    def __init__(
        self,
        settings: Settings,
        registry: Mapping[str, RunbookHandler] | None = None,
    ) -> None:
        self._settings = settings
        self._registry = RUNBOOKS if registry is None else registry

    async def run(self, request: RunbookRequest) -> RunbookResult:
        handler = self._registry.get(request.key)
        if handler is None:
            # Reported rather than ignored. A silent no-op would leave the platform believing a
            # remediation had been attempted, and the disagreement between the two allowlists is
            # exactly the thing that has to be loud.
            logger.error(
                "Refused a runbook this poller does not implement.",
                extra={"execution": request.execution_id, "runbook": request.key},
            )
            return RunbookResult(
                request.execution_id,
                FAILED,
                error=(
                    f"This poller does not implement the runbook '{request.key}'; "
                    "nothing was run."
                ),
            )

        try:
            return await handler(request, self._settings)
        except UnsafeParameterError as error:
            logger.error(
                "Refused a runbook whose parameters this poller does not accept.",
                extra={"execution": request.execution_id, "runbook": request.key},
            )
            return RunbookResult(request.execution_id, FAILED, error=str(error))
        except Exception as error:
            logger.exception(
                "A runbook raised.",
                extra={"execution": request.execution_id, "runbook": request.key},
            )
            return RunbookResult(
                request.execution_id, FAILED, error=f"The runbook raised: {error!r}"
            )
