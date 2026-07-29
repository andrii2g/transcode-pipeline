# SNS Checklist

Create two distinct Standard topics in `AwsNotifications:Region`:

- upload-completion topic published by the input S3 bucket;
- MediaConvert-state topic published by the exact EventBridge rule.

Apply `aws/sns/upload-topic-policy.json` and `aws/sns/mediaconvert-topic-policy.json` after replacing placeholders.

Subscribe these HTTPS endpoints:

```text
https://<public-api>/notifications/aws/sns/uploads
https://<public-api>/notifications/aws/sns/mediaconvert
```

Requirements:

- public DNS and a trusted public CA certificate;
- raw message delivery disabled;
- the API deployed before subscriptions are created;
- each exact topic ARN configured on its matching route;
- application role allowed to call `sns:ConfirmSubscription` on only those topics.

The application verifies the envelope signature, certificate URL and chain, message age, and exact topic before confirming. Never manually follow an unverified `SubscribeURL`.

Validate:

```powershell
aws sns get-topic-attributes --topic-arn <upload-topic-arn>
aws sns get-topic-attributes --topic-arn <mediaconvert-topic-arn>
aws sns list-subscriptions-by-topic --topic-arn <upload-topic-arn>
aws sns list-subscriptions-by-topic --topic-arn <mediaconvert-topic-arn>
```

Both subscriptions must leave `PendingConfirmation`. Configure SNS delivery status logging and alerts for endpoint failures.
