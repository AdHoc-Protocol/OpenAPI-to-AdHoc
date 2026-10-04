#!/usr/bin/env bash
# Downloads real OpenAPI / Swagger documents from their upstream repositories into samples/.
#   ./fetch-samples.sh
set -u
cd "$(dirname "$0")"
mkdir -p samples
OAI="https://raw.githubusercontent.com/OAI/learn.openapis.org/main/examples"
# <name in samples/> <url>
FILES="
petstore.yaml               $OAI/v3.0/petstore.yaml
petstore-expanded.yaml      $OAI/v3.0/petstore-expanded.yaml
uspto.yaml                  $OAI/v3.0/uspto.yaml
link-example.yaml           $OAI/v3.0/link-example.yaml
callback-example.yaml       $OAI/v3.0/callback-example.yaml
api-with-examples.yaml      $OAI/v3.0/api-with-examples.yaml
webhook-example.yaml        $OAI/v3.1/webhook-example.yaml
non-oauth-scopes.yaml       $OAI/v3.1/non-oauth-scopes.yaml
tictactoe.yaml              $OAI/v3.1/tictactoe.yaml
swagger-petstore-v3.yaml    https://raw.githubusercontent.com/swagger-api/swagger-petstore/master/src/main/resources/openapi.yaml
swagger-petstore-v2.json    https://petstore.swagger.io/v2/swagger.json
grafana.json                https://raw.githubusercontent.com/grafana/grafana/main/public/openapi3.json
docker-engine.yaml          https://raw.githubusercontent.com/moby/moby/master/api/swagger.yaml
"
# The page of a file in its repository, for a reader: raw.githubusercontent.com gives the bare text.
page() {
    case "$1" in
        https://raw.githubusercontent.com/*)
            local p="${1#https://raw.githubusercontent.com/}"
            local owner="${p%%/*}"; p="${p#*/}"
            local repo="${p%%/*}"; p="${p#*/}"
            echo "https://github.com/$owner/$repo/blob/$p" ;;
        *) echo "$1" ;;
    esac
}
{
    echo "# Where every sample comes from: <name in samples/> <page of the original>. Written by fetch-samples.sh;"
    echo "# the converter links these pages in the headers of the descriptions."
    while read -r n url; do
        [ -n "$n" ] && printf '%-27s %s\n' "$n" "$(page "$url")"
    done <<< "$FILES"
} > samples/sources.txt
status=0
while read -r n url; do
    [ -z "$n" ] && continue
    if curl -sSfL -A 'Mozilla/5.0' -o "samples/$n" "$url"; then echo "ok      $n"; else echo "FAILED  $n  $url"; status=1; fi
done <<< "$FILES"
exit $status
