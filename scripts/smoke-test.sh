#!/usr/bin/env bash
# ============================================================
#  Lumina CI — Comprehensive Smoke Test
#  Tests all API endpoints matching frontend actions
#  Usage: ./scripts/smoke-test.sh
# ============================================================

set -euo pipefail

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
DIM='\033[2m'
NC='\033[0m'

PASS=0
FAIL=0
SKIP=0
TOTAL=0

# API base URLs (direct service ports — no auth required)
BUILD_URL="${BUILD_URL:-http://localhost:5001}"
SECURITY_URL="${SECURITY_URL:-http://localhost:5002}"
SCANNER_URL="${SCANNER_URL:-http://localhost:5003}"
REPO_URL="${REPO_URL:-http://localhost:5004}"
GATEWAY_URL="${GATEWAY_URL:-http://localhost:5000}"
WEBAPP_URL="${WEBAPP_URL:-http://localhost:5005}"
NGINX_URL="${NGINX_URL:-http://localhost:80}"

# JWT token (obtained from gateway login)
JWT_TOKEN=""

# ============================================================
# Helpers
# ============================================================

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

check_contains() {
    local desc="$1"
    local haystack="$2"
    local needle="$3"
    TOTAL=$((TOTAL + 1))
    if echo "$haystack" | grep -q "$needle"; then
        echo -e "  ${GREEN}✅ $desc${NC}"
        PASS=$((PASS + 1))
    else
        echo -e "  ${RED}❌ $desc${NC} ${DIM}(expected to contain: $needle)${NC}"
        FAIL=$((FAIL + 1))
    fi
}

check_field() {
    local desc="$1"
    local json="$2"
    local field="$3"
    local expected="$4"
    TOTAL=$((TOTAL + 1))
    if [ -z "$json" ]; then
        echo -e "  ${RED}❌ $desc${NC} ${DIM}(empty response)${NC}"
        FAIL=$((FAIL + 1))
        return
    fi
    local actual
    actual=$(echo "$json" | jq -r "$field // empty" 2>/dev/null || echo "")
    if [ "$actual" = "$expected" ]; then
        echo -e "  ${GREEN}✅ $desc${NC}"
        PASS=$((PASS + 1))
    else
        echo -e "  ${RED}❌ $desc${NC} ${DIM}(expected: $expected, got: $actual)${NC}"
        FAIL=$((FAIL + 1))
    fi
}

# Check field matches any of several values (e.g. enum as string OR int)
check_field_oneof() {
    local desc="$1"
    local json="$2"
    local field="$3"
    shift 3
    local actual
    actual=$(echo "$json" | jq -r "$field // empty" 2>/dev/null || echo "")
    TOTAL=$((TOTAL + 1))
    for expected in "$@"; do
        if [ "$actual" = "$expected" ]; then
            echo -e "  ${GREEN}✅ $desc${NC}"
            PASS=$((PASS + 1))
            return
        fi
    done
    echo -e "  ${RED}❌ $desc${NC} ${DIM}(got: $actual, expected one of: $*)${NC}"
    FAIL=$((FAIL + 1))
}

skip() {
    local desc="$1"
    local reason="${2:-}"
    TOTAL=$((TOTAL + 1))
    echo -e "  ${YELLOW}⏭️  $desc ${DIM}($reason)${NC}"
    SKIP=$((SKIP + 1))
}

section() {
    echo ""
    echo -e "${YELLOW}$1${NC}"
}

# HTTP helpers — sets HTTP_CODE and HTTP_BODY
api_get() {
    local url="$1"
    local token="${2:-}"
    local response
    if [ -n "$token" ]; then
        response=$(curl -s -w "\n%{http_code}" -H "Authorization: Bearer $token" "$url" 2>/dev/null)
    else
        response=$(curl -s -w "\n%{http_code}" "$url" 2>/dev/null)
    fi
    HTTP_CODE=$(echo "$response" | tail -1)
    HTTP_BODY=$(echo "$response" | sed '$d')
}

api_post() {
    local url="$1"
    local data="$2"
    local token="${3:-}"
    local response
    if [ -n "$token" ]; then
        response=$(curl -s -w "\n%{http_code}" -X POST "$url" \
            -H "Content-Type: application/json" \
            -H "Authorization: Bearer $token" \
            -d "$data" 2>/dev/null)
    else
        response=$(curl -s -w "\n%{http_code}" -X POST "$url" \
            -H "Content-Type: application/json" \
            -d "$data" 2>/dev/null)
    fi
    HTTP_CODE=$(echo "$response" | tail -1)
    HTTP_BODY=$(echo "$response" | sed '$d')
}

# ============================================================
# Banner
# ============================================================
echo ""
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}   Lumina CI — Comprehensive Smoke Test${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"

# Check jq dependency
if ! command -v jq &>/dev/null; then
    echo -e "${RED}Error: jq is required. Install with: sudo yum install jq${NC}"
    exit 1
fi

# ============================================================
# 1. Docker Containers
# ============================================================
section "📦 1. Docker Containers"

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

# ============================================================
# 2. Infrastructure Health
# ============================================================
section "🔧 2. Infrastructure Health"

pg=$(docker exec lumina-postgres pg_isready -U lumina -d lumina_ci 2>&1)
check "Postgres ready" "$([[ "$pg" == *"accepting connections"* ]] && echo true || echo false)"

rd=$(docker exec lumina-redis redis-cli ping 2>&1)
check "Redis ready" "$([[ "$rd" == *"PONG"* ]] && echo true || echo false)"

rb=$(docker exec lumina-rabbitmq rabbitmq-diagnostics check_running 2>&1)
check "RabbitMQ running" "$([[ "$rb" == *"completed successfully"* ]] || [[ "$rb" == *"is running"* ]] && echo true || echo false)"

mn=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:9000/minio/health/live 2>/dev/null)
check "MinIO healthy (HTTP $mn)" "$([ "$mn" = "200" ] && echo true || echo false)"

# ============================================================
# 3. API Gateway Health & Auth
# ============================================================
section "🌐 3. API Gateway & Auth"

# 3a. Health (no auth)
api_get "$GATEWAY_URL/health"
check "Gateway /health (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

# 3b. Login as admin → get JWT
api_post "$GATEWAY_URL/api/auth/login" '{"username":"admin","password":"admin"}'
check "Auth login (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
if [ "$HTTP_CODE" = "200" ]; then
    JWT_TOKEN=$(echo "$HTTP_BODY" | jq -r '.token // empty' 2>/dev/null || echo "")
    check "JWT token received" "$([ -n "$JWT_TOKEN" ] && echo true || echo false)"
else
    JWT_TOKEN=""
    echo -e "  ${RED}⚠️  No JWT — gateway proxy tests will be skipped${NC}"
fi

# 3c. Swagger (dev only — may be disabled in Production)
api_get "$GATEWAY_URL/swagger/v1/swagger.json"
check "Gateway /swagger (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "404" ] && echo true || echo false)"

# ============================================================
# 4. Pipelines — CRUD (direct to build-service, no auth)
# ============================================================
section "🔄 4. Pipelines — CRUD"

# 4a. Create pipeline (simple, no git)
api_post "$BUILD_URL/api/pipelines" '{
    "name": "test-pipeline-simple",
    "description": "Simple test pipeline",
    "steps": [
        {"type": 0, "name": "Build RPM", "order": 1, "configuration": {}},
        {"type": 2, "name": "CVE Scan",   "order": 2, "configuration": {}},
        {"type": 1, "name": "PGP Sign",   "order": 3, "configuration": {}}
    ],
    "tags": ["test", "simple"]
}'
check "Create simple pipeline (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "201" ] && echo true || echo false)"
SIMPLE_PIPE_ID=""
if [ "$HTTP_CODE" = "201" ]; then
    SIMPLE_PIPE_ID=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
    check "Pipeline ID returned" "$([ -n "$SIMPLE_PIPE_ID" ] && echo true || echo false)"
fi

# 4b. Create pipeline with git settings
api_post "$BUILD_URL/api/pipelines" '{
    "name": "test-pipeline-git",
    "description": "Pipeline with git integration",
    "steps": [
        {"type": 0, "name": "Build RPM", "order": 1, "configuration": {}}
    ],
    "gitRepoUrl": "https://github.com/example/test-repo.git",
    "gitBranch": "main",
    "specPath": "package.spec",
    "buildImage": "lumina/rpm-build:latest",
    "gitUsername": "test-user",
    "gitToken": "test-token-123",
    "tags": ["test", "git"]
}'
check "Create git pipeline (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "201" ] && echo true || echo false)"
GIT_PIPE_ID=""
if [ "$HTTP_CODE" = "201" ]; then
    GIT_PIPE_ID=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")

    # Validate git fields in response (camelCase JSON)
    check_field "Response has gitRepoUrl" "$HTTP_BODY" '.data.gitRepoUrl' "https://github.com/example/test-repo.git"
    check_field "Response has gitBranch" "$HTTP_BODY" '.data.gitBranch' "main"
    check_field "Response has specPath" "$HTTP_BODY" '.data.specPath' "package.spec"
    check_field "Response has buildImage" "$HTTP_BODY" '.data.buildImage' "lumina/rpm-build:latest"
    check_field "Response has gitUsername" "$HTTP_BODY" '.data.gitUsername' "test-user"
    check_field "Response has hasGitToken=true" "$HTTP_BODY" '.data.hasGitToken' "true"
    # webhookUrl is constructed by controller, not null
    check_contains "Response has webhookUrl" "$HTTP_BODY" "webhookUrl"
fi

# 4c. Get pipeline by ID
if [ -n "$SIMPLE_PIPE_ID" ]; then
    api_get "$BUILD_URL/api/pipelines/$SIMPLE_PIPE_ID"
    check "Get pipeline by ID (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    check_field "Pipeline name matches" "$HTTP_BODY" '.data.name' "test-pipeline-simple"
    check_field "Pipeline has 3 steps" "$HTTP_BODY" '.data.steps | length' "3"
    # PipelineStatus enum: Active=1 (may serialize as "Active" or 1)
    check_field_oneof "Pipeline status is Active" "$HTTP_BODY" '.data.status' "Active" "1"
    check_field "Pipeline has 2 tags" "$HTTP_BODY" '.data.tags | length' "2"
fi

# 4d. List pipelines — response uses "pipelines" not "items"
api_get "$BUILD_URL/api/pipelines"
check "List pipelines (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
check_contains "Response has data.pipelines" "$HTTP_BODY" '"pipelines"'
PIPE_COUNT=$(echo "$HTTP_BODY" | jq -r '.data.totalCount // 0' 2>/dev/null || echo "0")
check "Pipelines count >= 2" "$([ "$PIPE_COUNT" -ge 2 ] && echo true || echo false)"

# 4e. Pipeline list summary includes git fields
if [ -n "$GIT_PIPE_ID" ]; then
    GIT_PIPE_IN_LIST=$(echo "$HTTP_BODY" | jq -r ".data.pipelines[] | select(.id == \"$GIT_PIPE_ID\")" 2>/dev/null || echo "")
    check "Git pipeline in list" "$([ -n "$GIT_PIPE_IN_LIST" ] && echo true || echo false)"
    if [ -n "$GIT_PIPE_IN_LIST" ]; then
        check_contains "List item has gitRepoUrl" "$GIT_PIPE_IN_LIST" "test-repo"
        check_contains "List item has gitBranch" "$GIT_PIPE_IN_LIST" "main"
    fi
fi

# 4f. Get non-existent pipeline
api_get "$BUILD_URL/api/pipelines/00000000-0000-0000-0000-000000000000"
check "Get non-existent pipeline → 404 (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "404" ] && echo true || echo false)"

# ============================================================
# 5. Builds — Trigger & Lifecycle
# ============================================================
section "🔨 5. Builds — Trigger & Lifecycle"

# 5a. Trigger manual build
BUILD_ID_1=""
if [ -n "$SIMPLE_PIPE_ID" ]; then
    api_post "$BUILD_URL/api/pipelines/$SIMPLE_PIPE_ID/trigger" '{
        "specName": "test-pkg",
        "specContent": "Name: test-pkg\nVersion: 1.0.0\nRelease: 1%{?dist}\nSummary: Test package\nLicense: MIT\n\n%description\nA test package for smoke testing.\n\n%prep\n%setup -q\n\n%build\n\n%install\nmkdir -p %{buildroot}/opt/test\necho hello > %{buildroot}/opt/test/hello.txt\n\n%files\n/opt/test/hello.txt",
        "sourceUrl": "https://example.com/test-1.0.0.tar.gz",
        "triggeredBy": "smoke-test"
    }'
    check "Trigger manual build (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    if [ "$HTTP_CODE" = "200" ]; then
        BUILD_ID_1=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
        # BuildStatus enum: Queued=0, but DockerBuildService may change it fast
        check_field_oneof "Build status is Queued/Building/Failed" "$HTTP_BODY" '.data.status' "Queued" "0" "Building" "1" "Failed" "3"
        check_field "Build specName matches" "$HTTP_BODY" '.data.specName' "test-pkg"
        check_field "Build has sourceUrl" "$HTTP_BODY" '.data.sourceUrl' "https://example.com/test-1.0.0.tar.gz"
        check_field "Build triggeredBy" "$HTTP_BODY" '.data.triggeredBy' "smoke-test"
    fi
else
    skip "Trigger manual build" "no pipeline ID"
fi

# 5b. Trigger auto build on non-git pipeline → should fail (400)
if [ -n "$SIMPLE_PIPE_ID" ]; then
    api_post "$BUILD_URL/api/pipelines/$SIMPLE_PIPE_ID/trigger-auto" '{"triggeredBy":"smoke-test"}'
    check "Auto-build on non-git pipeline → 400 (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "400" ] && echo true || echo false)"
fi

# 5c. Trigger auto build on git pipeline
BUILD_ID_2=""
if [ -n "$GIT_PIPE_ID" ]; then
    api_post "$BUILD_URL/api/pipelines/$GIT_PIPE_ID/trigger-auto" '{"triggeredBy":"smoke-test-auto"}'
    check "Trigger auto build on git pipeline (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    if [ "$HTTP_CODE" = "200" ]; then
        BUILD_ID_2=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
        # SourceUrl should contain git:// prefix
        check_contains "Auto build has git:// sourceUrl" "$HTTP_BODY" "git://"
        check_contains "Auto build has correct repo" "$HTTP_BODY" "test-repo"
    fi
else
    skip "Trigger auto build on git pipeline" "no git pipeline ID"
fi

# 5d. List builds — response uses "builds" not "items"
api_get "$BUILD_URL/api/builds"
check "List builds (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
check_contains "Response has data.builds" "$HTTP_BODY" '"builds"'

# 5e. Get build details
if [ -n "$BUILD_ID_1" ]; then
    api_get "$BUILD_URL/api/builds/$BUILD_ID_1"
    check "Get build details (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    check_field "Build detail: specName" "$HTTP_BODY" '.data.specName' "test-pkg"
    check_field "Build detail: pipelineId" "$HTTP_BODY" '.data.pipelineId' "$SIMPLE_PIPE_ID"
    check_field "Build detail: sourceUrl" "$HTTP_BODY" '.data.sourceUrl' "https://example.com/test-1.0.0.tar.gz"
    check_field "Build detail: has artifacts array" "$HTTP_BODY" '.data.artifacts | length' "0"
else
    skip "Get build details" "no build ID"
fi

# 5f. Get build logs
if [ -n "$BUILD_ID_1" ]; then
    api_get "$BUILD_URL/api/builds/$BUILD_ID_1/logs"
    check "Get build logs (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    check_contains "Logs response has success" "$HTTP_BODY" '"success"'
else
    skip "Get build logs" "no build ID"
fi

# 5g. Download spec file
if [ -n "$BUILD_ID_1" ]; then
    SPEC_RESPONSE=$(curl -s -w "\n%{http_code}" "$BUILD_URL/api/builds/$BUILD_ID_1/spec" 2>/dev/null)
    SPEC_CODE=$(echo "$SPEC_RESPONSE" | tail -1)
    SPEC_BODY=$(echo "$SPEC_RESPONSE" | sed '$d')
    check "Download spec file (HTTP $SPEC_CODE)" "$([ "$SPEC_CODE" = "200" ] && echo true || echo false)"
    check_contains "Spec contains Name:" "$SPEC_BODY" "Name:"
    check_contains "Spec contains Version:" "$SPEC_BODY" "Version:"
else
    skip "Download spec file" "no build ID"
fi

# 5h. Build queue — uses "queued", "running", "queuedCount", "runningCount"
api_get "$BUILD_URL/api/builds/queue"
check "Build queue (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
check_contains "Queue has queued array" "$HTTP_BODY" '"queued"'
check_contains "Queue has running array" "$HTTP_BODY" '"running"'
check_contains "Queue has queuedCount" "$HTTP_BODY" '"queuedCount"'
check_contains "Queue has runningCount" "$HTTP_BODY" '"runningCount"'

# 5i. Get non-existent build
api_get "$BUILD_URL/api/builds/00000000-0000-0000-0000-000000000000"
check "Get non-existent build → 404 (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "404" ] && echo true || echo false)"

# ============================================================
# 6. Webhooks
# ============================================================
section "🔔 6. Webhooks"

WEBHOOK_BUILD_ID=""
if [ -n "$GIT_PIPE_ID" ]; then
    # Simulate GitHub push webhook
    api_post "$BUILD_URL/api/webhooks/$GIT_PIPE_ID" '{
        "ref": "refs/heads/main",
        "before": "abc123",
        "after": "def456",
        "repository": {
            "clone_url": "https://github.com/example/test-repo.git",
            "full_name": "example/test-repo"
        },
        "pusher": {
            "name": "test-user",
            "email": "test@example.com"
        },
        "head_commit": {
            "id": "def456",
            "message": "Update package spec",
            "author": {
                "name": "Test User",
                "email": "test@example.com",
                "username": "test-user"
            }
        }
    }'
    check "Webhook push event (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    if [ "$HTTP_CODE" = "200" ]; then
        WEBHOOK_BUILD_ID=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
        if [ -n "$WEBHOOK_BUILD_ID" ]; then
            check_contains "Webhook build has git:// sourceUrl" "$HTTP_BODY" "git://"
            check_contains "Webhook build references main branch" "$HTTP_BODY" "main"
        fi
    fi
else
    skip "Webhook push event" "no git pipeline ID"
fi

# ============================================================
# 7. Security Service (direct, no auth)
# ============================================================
section "🔒 7. Security Service"

# 7a. Generate PGP key
api_post "$SECURITY_URL/api/security/keys/generate" '{
    "keyName": "smoke-test-key",
    "email": "smoke-test@lumina.1t.ru",
    "passphrase": "test-passphrase-12345"
}'
check "Generate PGP key (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "201" ] && echo true || echo false)"
PGP_KEY_ID=""
if [ "$HTTP_CODE" = "201" ]; then
    PGP_KEY_ID=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
fi

# 7b. List PGP keys
api_get "$SECURITY_URL/api/security/keys"
check "List PGP keys (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
check_contains "Keys response has data" "$HTTP_BODY" '"data"'

# 7c. Signing history
api_get "$SECURITY_URL/api/security/signing/history"
check "Signing history (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

# ============================================================
# 8. Scanner Service (direct, no auth)
# ============================================================
section "🛡️ 8. Scanner Service"

# 8a. Recent reports
api_get "$SCANNER_URL/api/scanner/reports/recent"
check "Recent scan reports (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

# 8b. Trivy server (port 8081 on host → 8080 in container)
TRIVY_CODE=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:8081/api/v1/status 2>/dev/null)
check "Trivy server (HTTP $TRIVY_CODE)" "$([ "$TRIVY_CODE" = "200" ] || [ "$TRIVY_CODE" = "404" ] && echo true || echo false)"

# ============================================================
# 9. Repository Service (direct, no auth)
# ============================================================
section "📁 9. Repository Service"

# 9a. Create repository
api_post "$REPO_URL/api/repository" '{
    "name": "smoke-test-repo",
    "displayName": "Smoke Test Repository",
    "basePath": "/packages/smoke-test",
    "arch": "x86_64",
    "distribution": "el9",
    "createdBy": "smoke-test"
}'
check "Create repository (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "201" ] || [ "$HTTP_CODE" = "409" ] && echo true || echo false)"
TEST_REPO_ID=""
if [ "$HTTP_CODE" = "201" ]; then
    TEST_REPO_ID=$(echo "$HTTP_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
fi

# 9b. List repositories
api_get "$REPO_URL/api/repository"
check "List repositories (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
check_contains "Repositories response has data" "$HTTP_BODY" '"data"'

# 9c. Create a dummy RPM file and upload it to the test repository
if [ -n "$TEST_REPO_ID" ]; then
    echo "  Creating dummy RPM file for upload test..."
    # Create a minimal valid RPM header (enough for the upload endpoint to accept it)
    # We use a simple binary blob with .rpm extension — the service will accept it
    DUMMY_RPM="/tmp/lumina-smoke-test-1.0.0-1.el9.x86_64.rpm"
    dd if=/dev/urandom of="$DUMMY_RPM" bs=1024 count=4 2>/dev/null

    # Upload via multipart/form-data
    UPLOAD_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "$REPO_URL/api/repository/upload" \
        -F "file=@$DUMMY_RPM" \
        -F "repositoryId=$TEST_REPO_ID" \
        -F "publishedBy=smoke-test" 2>/dev/null)
    UPLOAD_HTTP_CODE=$(echo "$UPLOAD_RESPONSE" | tail -1)
    UPLOAD_BODY=$(echo "$UPLOAD_RESPONSE" | sed '$d')
    check "Upload RPM to repository (HTTP $UPLOAD_HTTP_CODE)" "$([ "$UPLOAD_HTTP_CODE" = "200" ] && echo true || echo false)"
    if [ "$UPLOAD_HTTP_CODE" = "200" ]; then
        check_field "Upload response has success=true" "$UPLOAD_BODY" '.success' "true"
        UPLOADED_PKG_ID=$(echo "$UPLOAD_BODY" | jq -r '.data.id // empty' 2>/dev/null || echo "")
        check "Uploaded package has ID" "$([ -n "$UPLOADED_PKG_ID" ] && echo true || echo false)"
    fi

    # Clean up dummy RPM
    rm -f "$DUMMY_RPM"
else
    skip "Upload RPM to repository" "no test repo ID"
fi

# 9d. List packages in repository
if [ -n "$TEST_REPO_ID" ]; then
    api_get "$REPO_URL/api/repository/$TEST_REPO_ID/packages"
    check "List packages in repository (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    check_contains "Packages response has data array" "$HTTP_BODY" '"data"'
    PKG_COUNT=$(echo "$HTTP_BODY" | jq -r '.data | length' 2>/dev/null || echo "0")
    check "Repository has at least 1 package" "$([ "$PKG_COUNT" -ge 1 ] && echo true || echo false)"
else
    skip "List packages in repository" "no test repo ID"
fi

# 9e. Sync repository (runs createrepo_c --update)
if [ -n "$TEST_REPO_ID" ]; then
    api_post "$REPO_URL/api/repository/sync" "{\"repositoryId\":\"$TEST_REPO_ID\"}"
    check "Sync repository metadata (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
    if [ "$HTTP_CODE" = "200" ]; then
        check_field "Sync response success" "$HTTP_BODY" '.success' "true"
    fi
else
    skip "Sync repository metadata" "no test repo ID"
fi

# 9f. Check repodata was created (via filesystem in container)
if [ -n "$TEST_REPO_ID" ]; then
    # Get the basePath of the test repo from the list response
    REPO_BASEPATH=$(curl -s "$REPO_URL/api/repository" | jq -r '.data[] | select(.name == "smoke-test-repo") | .basePath // empty' 2>/dev/null || echo "")
    if [ -n "$REPO_BASEPATH" ]; then
        REPODATAMD=$(docker exec lumina-repository-service test -f "/app/repos${REPO_BASEPATH}/x86_64/repodata/repomd.xml" && echo "exists" || echo "missing")
        check "repodata/repomd.xml exists" "$([ "$REPODATAMD" = "exists" ] && echo true || echo false)"
    else
        skip "repodata/repomd.xml check" "could not determine basePath"
    fi
else
    skip "repodata/repomd.xml check" "no test repo ID"
fi

# ============================================================
# 10. API Gateway — Proxy Routes (requires JWT)
# ============================================================
section "🔀 10. API Gateway — Proxy Routes"

if [ -n "$JWT_TOKEN" ]; then
    api_get "$GATEWAY_URL/api/pipelines" "$JWT_TOKEN"
    check "Gateway → /api/pipelines (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

    api_get "$GATEWAY_URL/api/builds" "$JWT_TOKEN"
    check "Gateway → /api/builds (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

    api_get "$GATEWAY_URL/api/security/keys" "$JWT_TOKEN"
    check "Gateway → /api/security/keys (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

    api_get "$GATEWAY_URL/api/scanner/reports/recent" "$JWT_TOKEN"
    check "Gateway → /api/scanner/reports/recent (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"

    api_get "$GATEWAY_URL/api/repository" "$JWT_TOKEN"
    check "Gateway → /api/repository (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
else
    skip "Gateway → /api/pipelines" "no JWT token"
    skip "Gateway → /api/builds" "no JWT token"
    skip "Gateway → /api/security/keys" "no JWT token"
    skip "Gateway → /api/scanner/reports/recent" "no JWT token"
    skip "Gateway → /api/repository" "no JWT token"
fi

# Webhook route is anonymous (no auth needed)
if [ -n "$GIT_PIPE_ID" ]; then
    api_post "$GATEWAY_URL/api/webhooks/$GIT_PIPE_ID" '{
        "ref": "refs/heads/main",
        "repository": {"clone_url": "https://github.com/example/test-repo.git"},
        "head_commit": {"id": "abc789", "author": {"username": "gw-test"}}
    }'
    check "Gateway → /api/webhooks (anonymous) (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "200" ] && echo true || echo false)"
fi

# Gateway without auth → should be 401
api_get "$GATEWAY_URL/api/pipelines"
check "Gateway without auth → 401 (HTTP $HTTP_CODE)" "$([ "$HTTP_CODE" = "401" ] && echo true || echo false)"

# ============================================================
# 11. Web UI & Nginx Proxy
# ============================================================
section "🖥️ 11. Web UI & Consoles"

# 11a. Nginx (port 80) — serves Blazor via proxy to webapp container
NGINX_CODE=$(curl -s -o /dev/null -w "%{http_code}" "$NGINX_URL" 2>/dev/null)
check "Nginx (port 80) (HTTP $NGINX_CODE)" "$([ "$NGINX_CODE" = "200" ] && echo true || echo false)"

# 11b. WebApp container directly (serves Blazor static files)
WEBAPP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "$WEBAPP_URL" 2>/dev/null)
check "WebApp (port 5005) (HTTP $WEBAPP_CODE)" "$([ "$WEBAPP_CODE" = "200" ] && echo true || echo false)"

# 11c. Nginx API proxy (proxies /api/ to gateway, needs JWT)
if [ -n "$JWT_TOKEN" ]; then
    NGINX_API_CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $JWT_TOKEN" "$NGINX_URL/api/pipelines" 2>/dev/null)
    check "Nginx → /api/pipelines (with JWT) (HTTP $NGINX_API_CODE)" "$([ "$NGINX_API_CODE" = "200" ] && echo true || echo false)"
else
    skip "Nginx API proxy test" "no JWT token"
fi

# 11d. WebApp API proxy (proxies /api/ to gateway, needs JWT)
if [ -n "$JWT_TOKEN" ]; then
    WEBAPP_API_CODE=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $JWT_TOKEN" "$WEBAPP_URL/api/pipelines" 2>/dev/null)
    check "WebApp → /api/pipelines (with JWT) (HTTP $WEBAPP_API_CODE)" "$([ "$WEBAPP_API_CODE" = "200" ] && echo true || echo false)"
else
    skip "WebApp API proxy test" "no JWT token"
fi

# 11e. Management consoles
RABBIT_MGMT=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:15672 2>/dev/null)
check "RabbitMQ Management (HTTP $RABBIT_MGMT)" "$([ "$RABBIT_MGMT" = "200" ] && echo true || echo false)"

MINIO_CONSOLE=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:9001 2>/dev/null)
check "MinIO Console (HTTP $MINIO_CONSOLE)" "$([ "$MINIO_CONSOLE" = "200" ] || [ "$MINIO_CONSOLE" = "302" ] || [ "$MINIO_CONSOLE" = "403" ] && echo true || echo false)"

# ============================================================
# Summary
# ============================================================
echo ""
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}   Results: ${GREEN}$PASS passed${NC}, ${RED}$FAIL failed${NC}, ${YELLOW}$SKIP skipped${NC}, $TOTAL total"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"

if [ "$FAIL" -gt 0 ]; then
    echo -e "${RED}   Some tests failed! Check logs above.${NC}"
    exit 1
else
    echo -e "${GREEN}   All tests passed! 🎉${NC}"
    exit 0
fi