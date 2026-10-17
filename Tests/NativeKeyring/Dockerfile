FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends libsecret-1-0 gnome-keyring dbus-daemon && rm -rf /var/lib/apt/lists/*
WORKDIR /probe
COPY . .
RUN dotnet build Probe.csproj -c Release -o /probe/out
CMD ["bash", "/probe/run.sh"]
