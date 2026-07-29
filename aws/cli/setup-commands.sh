#!/usr/bin/env bash
set -euo pipefail

# Replace all placeholders before running.
REGION="eu-west-1"
ACCOUNT_ID="111122223333"
INPUT_BUCKET="replace-video-input"
OUTPUT_BUCKET="replace-video-output"
UPLOAD_TOPIC="demo-video-upload-completed-prod"
MEDIACONVERT_TOPIC="demo-mediaconvert-job-state-prod"
API_BASE_URL="https://media-api.example.com"

aws sns create-topic   --region "$REGION"   --name "$UPLOAD_TOPIC"

aws sns create-topic   --region "$REGION"   --name "$MEDIACONVERT_TOPIC"

aws sns subscribe   --region "$REGION"   --topic-arn "arn:aws:sns:${REGION}:${ACCOUNT_ID}:${UPLOAD_TOPIC}"   --protocol https   --notification-endpoint "${API_BASE_URL}/notifications/aws/sns/uploads"

aws sns subscribe   --region "$REGION"   --topic-arn "arn:aws:sns:${REGION}:${ACCOUNT_ID}:${MEDIACONVERT_TOPIC}"   --protocol https   --notification-endpoint "${API_BASE_URL}/notifications/aws/sns/mediaconvert"

# Apply the topic policies after replacing placeholders.
aws sns set-topic-attributes   --region "$REGION"   --topic-arn "arn:aws:sns:${REGION}:${ACCOUNT_ID}:${UPLOAD_TOPIC}"   --attribute-name Policy   --attribute-value file://aws/sns/upload-topic-policy.json

aws sns set-topic-attributes   --region "$REGION"   --topic-arn "arn:aws:sns:${REGION}:${ACCOUNT_ID}:${MEDIACONVERT_TOPIC}"   --attribute-name Policy   --attribute-value file://aws/sns/mediaconvert-topic-policy.json

aws s3api put-bucket-notification-configuration   --region "$REGION"   --bucket "$INPUT_BUCKET"   --notification-configuration file://aws/s3/notification-configuration.json

aws s3api put-bucket-cors   --region "$REGION"   --bucket "$INPUT_BUCKET"   --cors-configuration file://aws/s3/cors.json

aws s3api put-bucket-lifecycle-configuration   --region "$REGION"   --bucket "$INPUT_BUCKET"   --lifecycle-configuration file://aws/s3/lifecycle.json

# Create the EventBridge rule and target after the SNS target policy exists.
aws events put-rule   --region "$REGION"   --name "demo-mediaconvert-job-state-prod"   --event-pattern file://aws/eventbridge/mediaconvert-event-pattern.json   --state ENABLED

aws events put-targets   --region "$REGION"   --rule "demo-mediaconvert-job-state-prod"   --targets "Id"="MediaConvertSns","Arn"="arn:aws:sns:${REGION}:${ACCOUNT_ID}:${MEDIACONVERT_TOPIC}"

echo "SNS subscriptions remain PendingConfirmation until the application confirms them."
echo "Create and validate MediaConvert presets/template using docs/MEDIACONVERT-SETUP.md."
