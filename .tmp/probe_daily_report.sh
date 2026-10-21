#!/usr/bin/env bash
# Read-only runtime probe against the locally running app (localhost:5013).
# Uses the seeded admin credentials from user secrets; never echoes them.
set -u
BASE="http://localhost:5013"
SECRETS="$APPDATA/Microsoft/UserSecrets/cf47e1a6-87ac-4762-af69-fda50df6af6e/secrets.json"
JAR="$(mktemp)"

U=$(node -e "const s=require(process.argv[1]);console.log(s.InitialAdmin.Username||'')" "$SECRETS" 2>/dev/null)
P=$(node -e "const s=require(process.argv[1]);console.log(s.InitialAdmin.Password||'')" "$SECRETS" 2>/dev/null)
if [ -z "$U" ] || [ -z "$P" ]; then echo "FATAL: could not load admin credentials from user secrets"; exit 1; fi

# 1) GET login page (collect cookie + antiforgery token)
curl -sS -c "$JAR" -o /tmp/login.html "$BASE/Account/Login" -w 'login_page=%{http_code}\n'
TOKEN=$(grep -o '__RequestVerificationToken" type="hidden" value="[^"]*"' /tmp/login.html | sed 's/.*value="//; s/"$//')
if [ -z "$TOKEN" ]; then
  TOKEN=$(grep -o 'name="__RequestVerificationToken"[^>]*' /tmp/login.html | grep -o 'value="[^"]*"' | sed 's/value="//; s/"$//')
fi
if [ -z "$TOKEN" ]; then echo "FATAL: no antiforgery token found"; exit 1; fi

# 2) POST login (credentials via --data-urlencode, values never printed)
curl -sS -b "$JAR" -c "$JAR" -o /dev/null \
  --data-urlencode "Username=$U" \
  --data-urlencode "Password=$P" \
  --data-urlencode "RememberMe=false" \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  -w 'login_post=%{http_code} redirect=%{redirect_url}\n' \
  "$BASE/Account/Login"

# 3) GET the JSON report for the user's exact range (this invokes the provider)
echo '--- GET /DailyPerformance/Report?startDate=2026-10-14&endDate=2026-10-20 ---'
curl -sS -b "$JAR" -w '\nreport_http=%{http_code} total=%{time_total}s\n' \
  "$BASE/DailyPerformance/Report?startDate=2026-10-14&endDate=2026-10-20"

rm -f "$JAR" /tmp/login.html
