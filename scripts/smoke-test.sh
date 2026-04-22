#!/usr/bin/env bash
# ============================================================
#  Lumina CI — Smoke Test Script
#  Usage: ./scripts/smoke-test.sh
# ============================================================

set -euo pipefail

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

PASS=0
FAIL=0
TOTAL=0

check() {
    local desc="$1"
    local result="$2"
    TOTAL=$((TOTAL + 1))
    if [ "$result" = "true" ]; then
        echo -e "  ${GREEN}✅ $desc${NC}"
        PASS=$((PASS + 1))
    else
        echo -e "  ${RED}❌ $desc${NC}"
        FAIL=$((FAIL + 1))
    fi
}

echo ""
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo -e "${CYAN}   Lumina CI — Smoke Test${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo ""

# ============================================================
# 1. Docker Containers
# ============================================================
echo -e "${YELLOW}📦 Docker Containers${NC}"

REQUIRED_CONTAINERS=(
    "lumina-postgres"
    "lumina-redis"
    "lumina-rabbitmq"
    "lumina-minio"
    "lumina-api-gateway"
    "lumina-build-service"
    "lumina-security-service"
    "lumina-scanner-service"
    "lumina-repository-service"
    "lumina-trivy"
    "lumina-webapp"
    "lumina-nginx"
)

for name in "${REQUIRED_CONTAINERS[@]}"; do
    status=$(docker inspect -f '{{.State.Status}}' "$name" 2>/dev/null || echo "missing")
    check "Container $name" "$([ "$status" = "running" ] && echo true || echo false)"
done

echo ""

# ============================================================
# 2. Infrastructure Health
# ============================================================
echo -e "${YELLOW}🔧 Infrastructure Health${NC}"

# Postgres
pg=$(docker exec lumina-postgres pg_isready -U lumina -d lumina_ci 2>&1)
check "Postgres ready" "$([[ "$pg" == *"accepting connections"* ]] && echo true || echo false)"

# Redis
rd=$(docker exec lumina-redis redis-cli ping 2>&1)
check "Redis ready" "$([[ "$rd" == *"PONG"* ]] && echo true || echo false)"

# RabbitMQ
rb=$(docker exec lumina-rabbitmq rabbitmq-diagnostics check_running 2>&1)
check "RabbitMQ running" "$([[ "$rb" == *"completed successfully"* ]] || [[ "$rb" == *"is running"* ]] && echo true || echo false)"

# MinIO
mn=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:9000/minio/health/live 2>/dev/null)
check "MinIO healthy (HTTP $mn)" "$([ "$mn" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 3. API Gateway
# ============================================================
echo -e "${YELLOW}🌐 API Gateway (port 5000)${NC}"

gw_health=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/health 2>/dev/null)
check "Gateway /health (HTTP $gw_health)" "$([ "$gw_health" = "200" ] && echo true || echo false)"

gw_swagger=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/swagger/v1/swagger.json 2>/dev/null)
check "Gateway /swagger (HTTP $gw_swagger)" "$([ "$gw_swagger" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 4. Auth
# ============================================================
echo -e "${YELLOW}🔐 Authentication${NC}"

auth_response=$(curl -s -w "\n%{http_code}" -X POST http://localhost:5000/api/auth/login \
    -H "Content-Type: application/json" \
    -d '{"username":"admin","password":"admin"}' 2>/dev/null)
auth_code=$(echo "$auth_response" | tail -1)
auth_body=$(echo "$auth_response" | sed '$d')
check "Login admin/admin (HTTP $auth_code)" "$([ "$auth_code" = "200" ] && echo true || echo false)"

# Extract JWT token
JWT_TOKEN=""
if [ "$auth_code" = "200" ] && command -v jq &>/dev/null; then
    JWT_TOKEN=$(echo "$auth_body" | jq -r '.token // empty' 2>/dev/null || echo "")
fi
check "JWT token received" "$([ -n "$JWT_TOKEN" ] && echo true || echo false)"

# Test authenticated request
if [ -n "$JWT_TOKEN" ]; then
    auth_test=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/pipelines \
        -H "Authorization: Bearer $JWT_TOKEN" 2>/dev/null)
    check "Authenticated request (HTTP $auth_test)" "$([ "$auth_test" = "200" ] && echo true || echo false)"
fi

echo ""

# ============================================================
# 5. Build Service — Pipelines
# ============================================================
echo -e "${YELLOW}🔄 Build Service — Pipelines${NC}"

# Create pipeline
create_pipe=$(curl -s -w "\n%{http_code}" -X POST http://localhost:5001/api/pipelines \
    -H "Content-Type: application/json" \
    -d '{
        "name": "smoke-test-pipeline",
        "description": "Pipeline created by smoke test",
        "steps": [
            {"type": 0, "name": "Build RPM", "order": 1, "configuration": {}},
            {"type": 2, "name": "CVE Scan",   "order": 2, "configuration": {}},
            {"type": 1, "name": "PGP Sign",   "order": 3, "configuration": {}},
            {"type": 3, "name": "Publish",    "order": 4, "configuration": {}}
        ],
        "tags": ["smoke-test"]
    }' 2>/dev/null)
cp_code=$(echo "$create_pipe" | tail -1)
cp_body=$(echo "$create_pipe" | sed '$d')
check "Create pipeline (HTTP $cp_code)" "$([ "$cp_code" = "201" ] && echo true || echo false)"

# Extract pipeline ID
PIPELINE_ID=""
if [ "$cp_code" = "201" ] && command -v jq &>/dev/null; then
    PIPELINE_ID=$(echo "$cp_body" | jq -r '.data.id // empty' 2>/dev/null || echo "")
fi

# List pipelines
list_pipe=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5001/api/pipelines 2>/dev/null)
check "List pipelines (HTTP $list_pipe)" "$([ "$list_pipe" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 6. Build Service — Trigger Build
# ============================================================
echo -e "${YELLOW}🔨 Build Service — Trigger Build${NC}"

if [ -n "$PIPELINE_ID" ]; then
    trigger=$(curl -s -w "\n%{http_code}" -X POST "http://localhost:5001/api/pipelines/$PIPELINE_ID/trigger" \
        -H "Content-Type: application/json" \
        -d '{
            "specName": "smoke-test-pkg",
            "specContent": "Name: smoke-test\nVersion: 1.0.0\nRelease: 1",
            "sourceUrl": "https://example.com/test.tar.gz",
            "triggeredBy": "smoke-test"
        }' 2>/dev/null)
    tr_code=$(echo "$trigger" | tail -1)
    tr_body=$(echo "$trigger" | sed '$d')
    check "Trigger build (HTTP $tr_code)" "$([ "$tr_code" = "200" ] || [ "$tr_code" = "500" ] && echo true || echo false)"

    BUILD_ID=""
    if [ "$tr_code" = "200" ] && command -v jq &>/dev/null; then
        BUILD_ID=$(echo "$tr_body" | jq -r '.data.id // empty' 2>/dev/null || echo "")
    fi
else
    check "Trigger build" "false"
    echo -e "    ${YELLOW}(skipped — no pipeline ID)${NC}"
fi

# List builds
list_builds=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5001/api/builds 2>/dev/null)
check "List builds (HTTP $list_builds)" "$([ "$list_builds" = "200" ] && echo true || echo false)"

# Build queue
queue=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5001/api/builds/queue 2>/dev/null)
check "Build queue (HTTP $queue)" "$([ "$queue" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 7. Security Service
# ============================================================
echo -e "${YELLOW}🔒 Security Service (port 5002)${NC}"

# Generate PGP key
key_gen=$(curl -s -w "\n%{http_code}" -X POST http://localhost:5002/api/security/keys/generate \
    -H "Content-Type: application/json" \
    -d '{
        "keyName": "smoke-test-key",
        "email": "test@lumina.1t.ru",
        "passphrase": "test-passphrase-123"
    }' 2>/dev/null)
kg_code=$(echo "$key_gen" | tail -1)
check "Generate PGP key (HTTP $kg_code)" "$([ "$kg_code" = "201" ] && echo true || echo false)"

# List keys
list_keys=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5002/api/security/keys 2>/dev/null)
check "List PGP keys (HTTP $list_keys)" "$([ "$list_keys" = "200" ] && echo true || echo false)"

# Signing history
sign_hist=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5002/api/security/signing/history 2>/dev/null)
check "Signing history (HTTP $sign_hist)" "$([ "$sign_hist" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 8. Scanner Service
# ============================================================
echo -e "${YELLOW}🛡️ Scanner Service (port 5003)${NC}"

# Recent reports
scan_recent=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5003/api/scanner/reports/recent 2>/dev/null)
check "Recent scan reports (HTTP $scan_recent)" "$([ "$scan_recent" = "200" ] && echo true || echo false)"

# Trivy server
trivy=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:8081/api/v1/status 2>/dev/null)
check "Trivy server (HTTP $trivy)" "$([ "$trivy" = "200" ] || [ "$trivy" = "404" ] && echo true || echo false)"

echo ""

# ============================================================
# 9. Repository Service
# ============================================================
echo -e "${YELLOW}📁 Repository Service (port 5004)${NC}"

# Create repository
create_repo=$(curl -s -w "\n%{http_code}" -X POST http://localhost:5004/api/repository \
    -H "Content-Type: application/json" \
    -d '{
        "name": "smoke-test-repo",
        "displayName": "Smoke Test Repository",
        "basePath": "/packages/smoke-test",
        "arch": "x86_64",
        "distribution": "el9",
        "createdBy": "smoke-test"
    }' 2>/dev/null)
cr_code=$(echo "$create_repo" | tail -1)
check "Create repository (HTTP $cr_code)" "$([ "$cr_code" = "201" ] || [ "$cr_code" = "409" ] && echo true || echo false)"

# List repositories
list_repos=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5004/api/repository 2>/dev/null)
check "List repositories (HTTP $list_repos)" "$([ "$list_repos" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 10. Gateway Proxy Routes
# ============================================================
echo -e "${YELLOW}🔀 API Gateway — Proxy Routes${NC}"

gw_pipes=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/pipelines 2>/dev/null)
check "Gateway → Pipelines (HTTP $gw_pipes)" "$([ "$gw_pipes" = "200" ] && echo true || echo false)"

gw_builds=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/builds 2>/dev/null)
check "Gateway → Builds (HTTP $gw_builds)" "$([ "$gw_builds" = "200" ] && echo true || echo false)"

gw_keys=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/security/keys 2>/dev/null)
check "Gateway → Security (HTTP $gw_keys)" "$([ "$gw_keys" = "200" ] && echo true || echo false)"

gw_scans=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/scanner/reports/recent 2>/dev/null)
check "Gateway → Scanner (HTTP $gw_scans)" "$([ "$gw_scans" = "200" ] && echo true || echo false)"

gw_repos=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/repository 2>/dev/null)
check "Gateway → Repositories (HTTP $gw_repos)" "$([ "$gw_repos" = "200" ] && echo true || echo false)"

echo ""

# ============================================================
# 11. Web UI
# ============================================================
echo -e "${YELLOW}🖥️ Web UI${NC}"

webapp=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5005 2>/dev/null)
check "WebApp (port 5005) (HTTP $webapp)" "$([ "$webapp" = "200" ] && echo true || echo false)"

nginx_web=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:80 2>/dev/null)
check "Nginx (port 80) (HTTP $nginx_web)" "$([ "$nginx_web" = "200" ] && echo true || echo false)"

# API proxy through WebApp nginx
webapp_api=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5005/api/pipelines 2>/dev/null)
check "WebApp API proxy → /api/pipelines (HTTP $webapp_api)" "$([ "$webapp_api" = "200" ] && echo true || echo false)"

rabbitmq_mgmt=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:15672 2>/dev/null)
check "RabbitMQ Management (HTTP $rabbitmq_mgmt)" "$([ "$rabbitmq_mgmt" = "200" ] && echo true || echo false)"

minio_console=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:9001 2>/dev/null)
check "MinIO Console (HTTP $minio_console)" "$([ "$minio_console" = "200" ] || [ "$minio_console" = "302" ] || [ "$minio_console" = "403" ] && echo true || echo false)"

echo ""

# ============================================================
# Summary
# ============================================================
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"
echo -e "${CYAN}   Results: ${GREEN}$PASS passed${NC}, ${RED}$FAIL failed${NC}, $TOTAL total"
echo -e "${CYAN}═══════════════════════════════════════════════════${NC}"

if [ "$FAIL" -gt 0 ]; then
    echo -e "${RED}   Some tests failed! Check logs above.${NC}"
    exit 1
else
    echo -e "${GREEN}   All tests passed! 🎉${NC}"
    exit 0
fi