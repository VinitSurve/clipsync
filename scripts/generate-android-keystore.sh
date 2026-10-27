#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# ClipSync — Android Release Keystore Generator & GitHub Secrets Helper
# ==============================================================================
# This script generates a persistent, cryptographically secure 4096-bit RSA
# signing keystore for ClipSync Android releases and prints the values formatted
# for GitHub Actions Secrets.
#
# IMPORTANT:
# - Run this script once on your local machine.
# - Store the generated .jks keystore in a safe backup location (e.g., password manager vault).
# - NEVER commit the .jks file or passwords to Git.
# ==============================================================================

KEY_ALIAS="clipsync"
VALIDITY_DAYS=10000 # 27+ years
KEY_SIZE=4096

DEFAULT_OUTPUT_DIR="$HOME/.clipsync"
mkdir -p "$DEFAULT_OUTPUT_DIR"
KEYSTORE_PATH="$DEFAULT_OUTPUT_DIR/clipsync-release.jks"

echo "=========================================================="
echo "  ClipSync Android Release Keystore Setup"
echo "=========================================================="

if [ -f "$KEYSTORE_PATH" ]; then
  echo ""
  echo "⚠️  Found existing keystore at: $KEYSTORE_PATH"
  echo "To prevent breaking in-place updates for installed devices, we will NOT overwrite an existing keystore."
  read -r -p "Do you want to export GitHub Secrets from this existing keystore? (y/n) " choice
  case "$choice" in
    y|Y )
      read -r -s -p "Enter Keystore Password: " STORE_PASS
      echo ""
      read -r -s -p "Enter Key Password (or press Enter if same as store password): " KEY_PASS
      echo ""
      if [ -z "$KEY_PASS" ]; then
        KEY_PASS="$STORE_PASS"
      fi
      ;;
    * )
      echo "Aborted. Existing keystore preserved."
      exit 0
      ;;
  esac
else
  echo ""
  echo "Creating new release keystore at: $KEYSTORE_PATH"
  echo "Key Alias: $KEY_ALIAS | Key Size: RSA $KEY_SIZE | Validity: $VALIDITY_DAYS days"
  echo ""

  # Prompt for password
  while true; do
    read -r -s -p "Enter new Keystore Password (min 8 chars): " STORE_PASS
    echo ""
    if [ ${#STORE_PASS} -lt 8 ]; then
      echo "Password too short. Minimum 8 characters required."
      continue
    fi
    read -r -s -p "Confirm Keystore Password: " STORE_PASS_CONFIRM
    echo ""
    if [ "$STORE_PASS" != "$STORE_PASS_CONFIRM" ]; then
      echo "Passwords do not match. Please try again."
      continue
    fi
    break
  done

  KEY_PASS="$STORE_PASS"

  # Find keytool
  KEYTOOL_CMD="keytool"
  if ! command -v keytool &>/dev/null; then
    if [ -x "/opt/homebrew/opt/openjdk@17/bin/keytool" ]; then
      KEYTOOL_CMD="/opt/homebrew/opt/openjdk@17/bin/keytool"
    else
      echo "❌ Error: keytool not found in PATH or /opt/homebrew/opt/openjdk@17/bin/keytool."
      exit 1
    fi
  fi

  echo "Generating keystore with $KEYTOOL_CMD..."
  "$KEYTOOL_CMD" -genkeypair \
    -v \
    -keystore "$KEYSTORE_PATH" \
    -alias "$KEY_ALIAS" \
    -keyalg RSA \
    -keysize "$KEY_SIZE" \
    -validity "$VALIDITY_DAYS" \
    -storepass "$STORE_PASS" \
    -keypass "$KEY_PASS" \
    -dname "CN=ClipSync, OU=Engineering, O=ClipSync, L=Mumbai, ST=Maharashtra, C=IN"

  chmod 600 "$KEYSTORE_PATH"
  echo "✅ Keystore successfully created at: $KEYSTORE_PATH"
fi

# Base64 encode for GitHub Secrets
BASE64_KEYSTORE=$(base64 -i "$KEYSTORE_PATH" | tr -d '\n')

echo ""
echo "=========================================================="
echo "  GitHub Actions Secrets Configuration"
echo "=========================================================="
echo "Go to: https://github.com/VinitSurve/clipsync/settings/secrets/actions"
echo "Click 'New repository secret' and add the following 4 secrets:"
echo ""
echo "1. Name:  ANDROID_KEYSTORE_PASSWORD"
echo "   Value: $STORE_PASS"
echo ""
echo "2. Name:  ANDROID_KEY_ALIAS"
echo "   Value: $KEY_ALIAS"
echo ""
echo "3. Name:  ANDROID_KEY_PASSWORD"
echo "   Value: $KEY_PASS"
echo ""
echo "4. Name:  ANDROID_KEYSTORE_BASE64"
echo "   Value (copy full string below):"
echo "----------------------------------------------------------"
echo "$BASE64_KEYSTORE"
echo "----------------------------------------------------------"
echo ""
echo "🔒 BACKUP NOTICE:"
echo "Save a backup copy of '$KEYSTORE_PATH' in your password manager or secure vault."
echo "If this keystore is lost, future updates cannot be installed without uninstalling the app."
echo "=========================================================="
