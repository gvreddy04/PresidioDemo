# The feed worker image: the .NET worker with embedded CPython 3.12, Presidio and the bundled spaCy model.
# Build through deploy/docker-compose.yml (Local) or deploy/docker-compose.dev.yml (Dev), or directly:
#   docker build -t presidio-worker:dev .
# The image exposes no ports: the worker only opens native broker connections. The spaCy model is downloaded
# while the image is built; the running worker makes no HTTP calls or model downloads.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /source
COPY src/PresidioDemo.Core/PresidioDemo.Core.csproj src/PresidioDemo.Core/
COPY src/PresidioDemo.Messaging/PresidioDemo.Messaging.csproj src/PresidioDemo.Messaging/
COPY src/PresidioDemo.Messaging.Solace/PresidioDemo.Messaging.Solace.csproj src/PresidioDemo.Messaging.Solace/
COPY src/PresidioDemo.Worker/PresidioDemo.Worker.csproj src/PresidioDemo.Worker/
RUN dotnet restore src/PresidioDemo.Worker/PresidioDemo.Worker.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/PresidioDemo.Worker/PresidioDemo.Worker.csproj -c Release -a $TARGETARCH --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble
# Ubuntu 24.04 ships CPython 3.12, the version the worker is verified with; libpython is the library Python.NET loads.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3.12 python3.12-venv libpython3.12t64 \
    && rm -rf /var/lib/apt/lists/*
COPY requirements.txt /tmp/requirements.txt
RUN python3.12 -m venv /opt/venv \
    && /opt/venv/bin/pip install --no-cache-dir -r /tmp/requirements.txt \
    && rm /tmp/requirements.txt
WORKDIR /app
COPY --from=build /app .
# An empty suffix-list URL list makes tldextract (used by Presidio's email recognizer) use its bundled snapshot
# instead of downloading the public suffix list at runtime.
ENV Presidio__PythonEnvironment=/opt/venv \
    PYTHONDONTWRITEBYTECODE=1 \
    TLDEXTRACT_PUBLIC_SUFFIX_LIST_URLS="" \
    TLDEXTRACT_CACHE=/tmp/tldextract
USER $APP_UID
ENTRYPOINT ["dotnet", "PresidioDemo.Worker.dll"]
