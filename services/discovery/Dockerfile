# Python 3.13 per CONVENTIONS.md; slim rather than alpine because httpx and aio-pika both ship
# manylinux wheels and musl would build them from source for no gain.
FROM python:3.13-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

WORKDIR /app

COPY pyproject.toml README.md ./
COPY src ./src

RUN pip install --no-cache-dir .

# Not root: this service holds a bus credential and a client secret, and nothing it does needs
# privileges. A subnet sweep is thousands of pings, which is the one thing that comes close, and it is
# granted the same way the poller's is — an ICMP datagram socket for this uid's group, set by AppHost
# through `net.ipv4.ping_group_range`. Changing this number without changing it there stops every
# sweep, and the only symptom is a scan that finds nothing.
RUN useradd --create-home --uid 10001 discovery
USER discovery

CMD ["python", "-m", "discovery"]
