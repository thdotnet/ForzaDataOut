#!/usr/bin/env bash
set -euo pipefail

prompt() {
  local var_name="$1"
  local label="$2"
  local default_value="${3:-}"
  local input

  if [[ -n "$default_value" ]]; then
    read -r -p "${label} [${default_value}]: " input
    input="${input:-$default_value}"
  else
    read -r -p "${label}: " input
  fi

  export "$var_name=$input"
}

die() {
  echo "ERROR: $*" >&2
  exit 1
}

command -v az >/dev/null 2>&1 || die "Azure CLI (az) not found. Install it and retry."
az account show >/dev/null 2>&1 || die "Not logged in. Run: az login"

echo "== Azure Event Hubs setup =="

prompt AZ_REGION "Preferred Azure region (e.g., southcentralus)" "southcentralus"
prompt RG_NAME   "Resource group name" "rg-eventhub-demo"
prompt EH_NAME   "Event Hub name" "eh-ingestion"

BASE_NS="$(echo "ehns-${RG_NAME}" | tr '[:upper:]' '[:lower:]' | tr -cd 'a-z0-9-')"
BASE_NS="${BASE_NS:0:30}"
RAND_SUFFIX="$(LC_ALL=C tr -dc 'a-z0-9' </dev/urandom | head -c 6 || true)"
NS_NAME="${BASE_NS}-${RAND_SUFFIX}"

SAS_RULE_NAME="send-policy"

echo
echo "Region:        ${AZ_REGION}"
echo "ResourceGroup: ${RG_NAME}"
echo "Namespace:     ${NS_NAME}"
echo "EventHub:      ${EH_NAME}"
echo "SAS Rule:      ${SAS_RULE_NAME}"
echo

echo "1) Creating resource group..."
az group create \
  --name "${RG_NAME}" \
  --location "${AZ_REGION}" \
  --only-show-errors -o none

echo "2) Creating Event Hubs namespace..."
az eventhubs namespace create \
  --name "${NS_NAME}" \
  --resource-group "${RG_NAME}" \
  --location "${AZ_REGION}" \
  --sku Standard \
  --only-show-errors -o none

echo "3) Creating Event Hub..."
az eventhubs eventhub create \
  --name "${EH_NAME}" \
  --namespace-name "${NS_NAME}" \
  --resource-group "${RG_NAME}" \
  --partition-count 2 \
  --only-show-errors -o none

echo "4) Creating Send-only SAS policy on the Event Hub..."
az eventhubs eventhub authorization-rule create \
  --resource-group "${RG_NAME}" \
  --namespace-name "${NS_NAME}" \
  --eventhub-name "${EH_NAME}" \
  --name "${SAS_RULE_NAME}" \
  --rights Send \
  --only-show-errors -o none

echo "5) Fetching SAS keys + connection string..."
# creation/update may take a few seconds to take effect [3](https://learn.microsoft.com/en-us/cli/azure/eventhubs/eventhub/authorization-rule?view=azure-cli-latest)
PRIMARY_KEY=""
PRIMARY_CS=""

for i in {1..12}; do
  PRIMARY_KEY="$(az eventhubs eventhub authorization-rule keys list \
    --resource-group "${RG_NAME}" \
    --namespace-name "${NS_NAME}" \
    --eventhub-name "${EH_NAME}" \
    --name "${SAS_RULE_NAME}" \
    --query primaryKey -o tsv 2>/dev/null || true)"

  PRIMARY_CS="$(az eventhubs eventhub authorization-rule keys list \
    --resource-group "${RG_NAME}" \
    --namespace-name "${NS_NAME}" \
    --eventhub-name "${EH_NAME}" \
    --name "${SAS_RULE_NAME}" \
    --query primaryConnectionString -o tsv 2>/dev/null || true)"

  if [[ -n "${PRIMARY_KEY}" && -n "${PRIMARY_CS}" ]]; then
    break
  fi
  sleep 2
done

[[ -n "${PRIMARY_CS}" ]] || die "Could not retrieve connection string. Try running the keys list command manually."

echo
echo "== Done =="
echo "Event Hubs Namespace: ${NS_NAME}"
echo "Event Hub:            ${EH_NAME}"
echo "SAS Policy:           ${SAS_RULE_NAME}"
echo
echo "PRIMARY KEY:"
echo "${PRIMARY_KEY}"
echo
echo "PRIMARY CONNECTION STRING (Send-only):"
echo "${PRIMARY_CS}"
echo
echo "Tip: export EVENTHUB_CONNECTION_STRING='${PRIMARY_CS}'"