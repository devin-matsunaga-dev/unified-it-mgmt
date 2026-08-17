from __future__ import annotations

from dataclasses import replace
from typing import Any

import pytest

from poller.runbooks import (
    FAILED,
    SUCCEEDED,
    TIMED_OUT,
    RunbookRequest,
    RunbookResult,
    RunbookRunner,
    UnsafeParameterError,
    build_argv,
)
from poller.settings import Settings

SETTINGS = Settings(
    name="poller-1",
    poller_group="default",
    agent_version="0.1.0",
    interval_seconds=15,
    api_base_url="http://localhost:5000",
    oidc_token_url="http://localhost:8080/token",
    oidc_client_id="it-platform-poller",
    oidc_client_secret="secret",
    amqp_url="amqp://poller:secret@localhost:5672/",
    heartbeat_exchange="Contracts.Events:PollerHeartbeat",
    telemetry_exchange="Contracts.Events:DeviceTelemetryReported",
    reachability_exchange="Contracts.Events:DeviceReachabilityChanged",
    http_timeout_seconds=10.0,
    max_concurrent_checks=50,
    icmp_privileged=True,
    runbooks_enabled=True,
    runbook_restart_service_command="/bin/echo restarted {service}",
)


def request(
    key: str = "restart-service",
    parameters: dict[str, str] | None = None,
    timeout_seconds: int = 10,
) -> RunbookRequest:
    return RunbookRequest(
        execution_id="0199c0de-0000-7000-8000-000000000001",
        key=key,
        version=1,
        parameters={"service": "nginx"} if parameters is None else parameters,
        timeout_seconds=timeout_seconds,
        device_id="0199c0de-3300-7000-8000-000000000006",
        address="http-target",
    )


# ---- the allowlist ----


async def test_run_a_runbook_this_agent_does_not_implement_is_refused_and_reported() -> None:
    """
    The agent half of the allowlist. A key it does not know is refused *and reported as a failure*
    rather than ignored, so a server and an agent that disagree escalate to a human instead of the
    platform believing a remediation was attempted.
    """
    result = await RunbookRunner(SETTINGS).run(request(key="delete-everything"))

    assert result.outcome == FAILED
    assert "does not implement" in (result.error or "")


async def test_run_the_restart_service_runbook_runs_the_template_and_reports_its_output() -> None:
    result = await RunbookRunner(SETTINGS).run(request())

    assert result.outcome == SUCCEEDED
    assert result.exit_code == 0
    assert result.output == "restarted nginx"


async def test_run_a_runbook_with_no_service_named_fails_without_running_anything() -> None:
    result = await RunbookRunner(SETTINGS).run(request(parameters={}))

    assert result.outcome == FAILED
    assert "No service was named" in (result.error or "")


async def test_run_a_command_that_exits_non_zero_is_a_failure_carrying_the_exit_code() -> None:
    settings = replace(SETTINGS, runbook_restart_service_command="/bin/false {service}")

    result = await RunbookRunner(settings).run(request())

    assert result.outcome == FAILED
    assert result.exit_code == 1


async def test_run_a_command_that_does_not_exist_is_a_failure_rather_than_a_crash() -> None:
    settings = replace(
        SETTINGS, runbook_restart_service_command="/nonexistent/restart {service}"
    )

    result = await RunbookRunner(settings).run(request())

    assert result.outcome == FAILED
    assert "could not be started" in (result.error or "")


async def test_run_a_handler_that_raises_is_a_failure_rather_than_a_crash() -> None:
    async def explode(_: RunbookRequest, __: Settings) -> RunbookResult:
        raise RuntimeError("the host is on fire")

    runner = RunbookRunner(SETTINGS, registry={"restart-service": explode})

    result = await runner.run(request())

    assert result.outcome == FAILED
    assert "the host is on fire" in (result.error or "")


# ---- the timeout ----


async def test_run_a_command_that_outlasts_its_timeout_is_timed_out_and_killed() -> None:
    settings = replace(SETTINGS, runbook_restart_service_command="/bin/sleep 30")

    result = await RunbookRunner(settings).run(request(timeout_seconds=1))

    assert result.outcome == TIMED_OUT
    assert "did not finish within 1s" in (result.error or "")


# ---- argv construction: the free-text execution path that does not exist ----


def test_build_argv_a_parameter_with_a_shell_metacharacter_is_refused() -> None:
    """
    The failure path that matters most in this file. A unit name carrying `; rm -rf /` is refused by
    the agent's own check — it never reaches an argv element, and there is no shell for it to reach
    even if it did.
    """
    with pytest.raises(UnsafeParameterError):
        build_argv("systemctl restart {service}", service="nginx; rm -rf /")


@pytest.mark.parametrize(
    "value",
    [
        "nginx && reboot",
        "nginx\nreboot",
        "$(reboot)",
        "`reboot`",
        "../../etc/passwd\x00",
        "nginx |tee /etc/shadow",
        "-nginx",
        "",
    ],
)
def test_build_argv_a_value_that_is_not_a_simple_name_is_refused(value: str) -> None:
    with pytest.raises(UnsafeParameterError):
        build_argv("systemctl restart {service}", service=value)


def test_build_argv_a_template_with_several_words_keeps_them_as_one_argument_each() -> None:
    """
    The template is split into words before substitution, which is why a parameter can never
    become a second word: whatever it contains, it lands inside the element the placeholder was in.
    """
    argv = build_argv("/usr/bin/sudo -n systemctl restart {service}", service="nginx.service")

    assert argv == ["/usr/bin/sudo", "-n", "systemctl", "restart", "nginx.service"]


def test_build_argv_an_empty_template_is_refused() -> None:
    with pytest.raises(ValueError, match="cannot be empty"):
        build_argv("   ", service="nginx")


# ---- parsing what the platform sends ----


def test_parse_a_dispatched_execution_reads_its_fields() -> None:
    payload: dict[str, Any] = {
        "executionId": "0199c0de-0000-7000-8000-000000000009",
        "runbookKey": "restart-service",
        "runbookVersion": 3,
        "deviceId": "0199c0de-3300-7000-8000-000000000006",
        "ciId": "0199c0de-2200-7000-8000-000000000006",
        "ciName": "Customer portal",
        "address": "http-target",
        "parameters": {"service": "nginx"},
        "timeoutSeconds": 45,
        "deadlineAt": "2026-08-17T00:01:00+00:00",
    }

    parsed = RunbookRequest.parse(payload)

    assert parsed.key == "restart-service"
    assert parsed.version == 3
    assert parsed.parameters == {"service": "nginx"}
    assert parsed.timeout_seconds == 45


def test_parse_a_dispatch_without_an_execution_id_raises() -> None:
    with pytest.raises(KeyError):
        RunbookRequest.parse({"runbookKey": "restart-service"})
