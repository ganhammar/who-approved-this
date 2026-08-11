# The linux-arm64 Native AOT binary is built in CI (no SDK layer here);
# this image only wraps it in the runtime dependencies
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

COPY publish/WhoApprovedThis.Agent /app/agent
EXPOSE 8080
ENTRYPOINT ["/app/agent"]
