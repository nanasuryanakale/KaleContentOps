#!/usr/bin/env bash
# Read-only: render the Daily Performance HTML page and check key values.
set -u
BASE="http://localhost:5013"
SECRETS="$APPDATA/Microsoft/UserSecrets/cf47e1a6-87ac-4762-af69-fda50df6af6e/secrets.json"
JAR="$(mktemp)"

U=$(node -e "const s=require(process.argv[1]);console.log(s.InitialAdmin.Username||'')" "$SECRETS" 2>/dev/null)
P=$(node -e "const s=require(process.argv[1]);console.log(s.InitialAdmin.Password||'')" "$SECRETS" 2>/dev/null)

curl -sS -c "$JAR" -o /tmp/login2.html "$BASE/Account/Login"
TOKEN=$(grep -o '__RequestVerificationToken" type="hidden" value="[^"]*"' /tmp/login2.html | sed 's/.*value="//; s/"$//')
curl -sS -b "$JAR" -c "$JAR" -o /dev/null \
  --data-urlencode "Username=$U" --data-urlencode "Password=$P" \
  --data-urlencode "RememberMe=false" \
  --data-urlencode "__RequestVerificationToken=$TOKEN" \
  "$BASE/Account/Login"

curl -sS -b "$JAR" -w '\npage_http=%{http_code} total=%{time_total}s\n' -o /tmp/dp.html \
  "$BASE/DailyPerformance?startDate=2026-10-14&endDate=2026-10-20"

echo '--- checks in rendered HTML ---'
for needle in "185.855" "356.460" "6 dari 7" "38.771" "23.881" "Video Views"; do
  if grep -qF "$needle" /tmp/dp.html; then echo "FOUND: $needle"; else echo "MISSING: $needle"; fi
done
echo '--- em-dash count (unavailable slots) ---'
grep -oF '—' /tmp/dp.html | wc -l
rm -f "$JAR" /tmp/login2.html
