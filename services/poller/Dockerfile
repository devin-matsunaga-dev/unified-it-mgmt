# Python 3.13 per CONVENTIONS.md; slim rather than alpine because httpx and aio-pika both ship
# manylinux wheels and musl would build them from source for no gain.
FROM python:3.13-slim

ENV PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

WORKDIR /app

COPY pyproject.toml README.md ./
COPY src ./src

RUN pip install --no-cache-dir .

# Not root: the poller holds a bus credential and a client secret, and nothing it does needs
# privileges.
RUN useradd --create-home --uid 10001 poller
USER poller

CMD ["python", "-m", "poller"]
