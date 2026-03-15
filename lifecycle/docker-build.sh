#!/bin/bash
# docker-build.sh — Build Docker image, push to private registry, trigger Watchtower.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/.."

# Load deployment config (REGISTRY, NAS_IP, WATCHTOWER_PORT, WATCHTOWER_TOKEN)
DEPLOY_ENV="secrets/deploy.agent_visible_env"
if [ ! -f "$DEPLOY_ENV" ]; then
    echo "ERROR: $DEPLOY_ENV not found. Copy from secrets/example.deploy.env and fill in values."
    exit 1
fi
while IFS='=' read -r key value; do
    key="${key//$'\r'/}"
    value="${value//$'\r'/}"
    [[ -z "$key" || "$key" =~ ^# ]] && continue
    export "$key"="$value"
done < "$DEPLOY_ENV"

for var in REGISTRY NAS_IP WATCHTOWER_PORT WATCHTOWER_TOKEN; do
    if [ -z "${!var:-}" ]; then
        echo "ERROR: $var not set in $DEPLOY_ENV"
        exit 1
    fi
done

IMAGE_NAME="skipdetector"
FULL_NAME="${REGISTRY}/${IMAGE_NAME}"
TIMESTAMP=$(date +"%Y%m%d%H%M")

echo "=== Building skipdetector (${TIMESTAMP}) ==="

# Tag current latest as rollback before overwriting
echo "Pulling current latest for rollback tag..."
if docker pull "${FULL_NAME}:latest" 2>/dev/null; then
    echo "Tagging current latest as rollback..."
    docker tag "${FULL_NAME}:latest" "${FULL_NAME}:rollback"
    docker push "${FULL_NAME}:rollback"
    echo "Rollback tag updated."
else
    echo "No existing latest found — skipping rollback tag."
fi

echo ""
echo "Building ${FULL_NAME}:${TIMESTAMP}..."
docker build -t "${FULL_NAME}:${TIMESTAMP}" .

docker tag "${FULL_NAME}:${TIMESTAMP}" "${FULL_NAME}:latest"

echo "Pushing ${TIMESTAMP} and latest to ${REGISTRY}..."
docker push "${FULL_NAME}:${TIMESTAMP}"
docker push "${FULL_NAME}:latest"

echo ""
echo "Done! Pushed:"
echo "  ${FULL_NAME}:${TIMESTAMP}"
echo "  ${FULL_NAME}:latest"
echo "  ${FULL_NAME}:rollback (previous latest)"

# Trigger Watchtower to pull and redeploy immediately
if [ -n "${WATCHTOWER_TOKEN:-}" ]; then
    echo ""
    echo "Triggering Watchtower redeploy..."
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "Authorization: Bearer ${WATCHTOWER_TOKEN}" \
        "http://${NAS_IP}:${WATCHTOWER_PORT}/v1/update" 2>/dev/null || echo "000")
    if [ "$HTTP_CODE" = "200" ]; then
        echo "Watchtower accepted the update request. Container will restart shortly."
    else
        echo "Warning: Watchtower returned HTTP ${HTTP_CODE}. Check NAS logs."
        echo "  Manual deploy: docker-compose pull && docker-compose up -d"
    fi
fi
echo ""
echo "To rollback: docker pull ${FULL_NAME}:rollback && docker tag ${FULL_NAME}:rollback ${FULL_NAME}:latest && docker push ${FULL_NAME}:latest"
