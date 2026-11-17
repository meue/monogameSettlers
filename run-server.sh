#!/bin/sh
# The multiplayer server on this machine (LAN), without Docker: ./run-server.sh [port], default 8080.
# Limits and the like as environment variables, e.g. Limits__StallSeconds=600 ./run-server.sh (src/Homestead.Server/README.md).
set -e
cd "$(dirname "$0")"
port="${1:-8080}"
dotnet build src/Homestead.Server -c Release
echo "In the game: localhost:$port, from other computers:"
for ip in $(hostname -I 2>/dev/null); do
  case "$ip" in *:*|172.17.*) ;; *) echo "  $ip:$port" ;; esac # IPv4 only, no Docker bridge
done
ASPNETCORE_URLS="http://0.0.0.0:$port" exec dotnet src/Homestead.Server/bin/Release/net9.0/Homestead.Server.dll
