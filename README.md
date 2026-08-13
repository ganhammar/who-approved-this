# who-approved-this

Companion repository for the blog post on delegated authorization for AI agents with Amazon Bedrock AgentCore Identity, in C#.

An expense approval assistant where the agent acts on behalf of the signed-in user. The same prompt gives different outcomes for an employee and a manager, and the audit trail names the human.

## Structure

```
.
├── .github/workflows/deploy.yml          # Build (Native AOT on arm64) + deploy
├── infra/
│   └── WhoApprovedThis.Infrastructure/   # CDK stack (C#)
├── src/
│   ├── WhoApprovedThis.Agent/            # AgentCore Runtime container: Bedrock Converse loop + MCP client
│   └── WhoApprovedThis.McpServer/        # OAuth-protected MCP server on Lambda (Native AOT)
├── frontend/                             # Static HTML/JS chat with Cognito PKCE login
└── scripts/postdeploy.sh                 # Wiring CloudFormation cannot express
```

## Deploy

Deployment runs in GitHub Actions on an arm64 runner, where the Native AOT binaries compile natively for the linux-arm64 Lambda and container targets.

Prerequisites, once per account:

1. CDK bootstrapped in `eu-north-1` (`npx aws-cdk bootstrap`)
2. Amazon Bedrock model access for Nova Pro (EU cross-region inference)
3. An IAM role for GitHub Actions with OIDC trust to this repository and permissions to deploy the stack

Repository secrets:

- `AWS_DEPLOY_ROLE_ARN`: the OIDC deploy role
- `DEMO_PASSWORD`: password set for the demo users `alice` and `bob`

Push to `main` (or run the workflow manually) to build and deploy. The workflow publishes both AOT binaries, runs `cdk deploy`, and finishes with [postdeploy.sh](scripts/postdeploy.sh), which points the agent client's OAuth callback at the AgentCore Identity credential provider, sets the demo users' passwords, and publishes the frontend. The `SiteUrl` stack output is the app.

Read more at [ganhammar.se](https://www.ganhammar.se).
