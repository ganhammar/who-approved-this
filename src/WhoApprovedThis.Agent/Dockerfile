# The linux-arm64 Native AOT binary is built in CI (no SDK layer here);
# this image only wraps it in the runtime dependencies
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

# Without a WORKDIR the ASP.NET content root is /, and its file watcher
# recursively walks the entire filesystem; on the AgentCore microVM that
# walk never finishes and the app hangs before binding the port
WORKDIR /app

COPY publish/WhoApprovedThis.Agent /app/agent
EXPOSE 8080
ENTRYPOINT ["/app/agent"]
