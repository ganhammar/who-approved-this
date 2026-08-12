#!/usr/bin/env bash
# Post-deploy wiring that CloudFormation cannot express:
#  1. Point the agent client's callback URL at the credential provider
#     (circular dependency between the two resources, see the stack)
#  2. Set permanent passwords for the demo users
#  3. Generate frontend/config.js from stack outputs and publish the site
set -euo pipefail

OUTPUTS=${1:-outputs.json}
: "${DEMO_PASSWORD:?Set DEMO_PASSWORD (used for both alice and bob)}"

get() {
  python3 -c "import json; print(json.load(open('$OUTPUTS'))['WhoApprovedThis']['$1'])"
}

POOL_ID=$(get UserPoolId)
AGENT_CLIENT_ID=$(get AgentClientId)
PROVIDER_CALLBACK=$(get ProviderCallbackUrl)
HOSTED_UI_BASE=$(get HostedUiBase)
FRONTEND_CLIENT_ID=$(get FrontendClientId)
RUNTIME_ARN=$(get AgentRuntimeArn)
SITE_BUCKET=$(get SiteBucket)
SITE_URL=$(get SiteUrl)
DISTRIBUTION_ID=$(get DistributionId)

# 1. update-user-pool-client REPLACES the client's OAuth configuration, so
# everything from the stack definition must be restated here
aws cognito-idp update-user-pool-client \
  --user-pool-id "$POOL_ID" \
  --client-id "$AGENT_CLIENT_ID" \
  --callback-urls "$PROVIDER_CALLBACK" \
  --allowed-o-auth-flows code \
  --allowed-o-auth-flows-user-pool-client \
  --allowed-o-auth-scopes openid expenses/read expenses/write expenses/approve \
  --supported-identity-providers COGNITO > /dev/null
echo "Agent client callback set to $PROVIDER_CALLBACK"

# 2. The runtime's workload identity must allowlist the return URL used in
# the 3LO consent flow (session binding)
WORKLOAD_IDENTITY="${RUNTIME_ARN##*/}"
aws bedrock-agentcore-control update-workload-identity \
  --name "$WORKLOAD_IDENTITY" \
  --allowed-resource-oauth2-return-urls "$SITE_URL" "http://localhost:4000/" > /dev/null
echo "Return URLs allowlisted on $WORKLOAD_IDENTITY"

# 3. Demo users
for user in alice bob; do
  aws cognito-idp admin-set-user-password \
    --user-pool-id "$POOL_ID" --username "$user" \
    --password "$DEMO_PASSWORD" --permanent
done
echo "Passwords set for alice and bob"

# 4. Frontend
cat > frontend/config.js <<EOF
window.CONFIG = {
  hostedUiBase: "$HOSTED_UI_BASE",
  clientId: "$FRONTEND_CLIENT_ID",
  agentArn: "$RUNTIME_ARN",
  redirectUri: "$SITE_URL",
};
EOF
aws s3 sync frontend "s3://$SITE_BUCKET" --delete --cache-control "max-age=60"
aws cloudfront create-invalidation --distribution-id "$DISTRIBUTION_ID" --paths "/*" > /dev/null
echo "Site published: $SITE_URL"
