# SNS HTTPS Endpoint

## Routes

```text
POST /notifications/aws/sns/uploads
POST /notifications/aws/sns/mediaconvert
```

Use separate expected TopicArn values.

## Subscription setup

1. Deploy the endpoint first.
2. Create HTTPS subscription.
3. SNS sends `SubscriptionConfirmation`.
4. Verify message signature and TopicArn.
5. Call `ConfirmSubscription` with token.
6. Confirm the subscription is active.
7. Keep raw delivery disabled.

## Request constraints

Suggested:

```text
maximum body: 512 KiB
timeout: short, for example 15-30 seconds
content type: accept text/plain JSON
rate limiting: generous enough for SNS; return 429 only when retry is desired
```

## Verification order

```text
bounded body read
parse envelope
validate required fields
validate TopicArn
validate SigningCertURL
fetch/cache certificate
validate certificate chain
build canonical message
verify signature
check timestamp policy
deduplicate MessageId
process type
```

## Canonical fields

The fields included depend on message type.

### Notification

```text
Message
MessageId
Subject (when present)
Timestamp
TopicArn
Type
```

### SubscriptionConfirmation / UnsubscribeConfirmation

```text
Message
MessageId
SubscribeURL
Timestamp
Token
TopicArn
Type
```

Follow the exact AWS canonical ordering and line-break rules.

## HTTP results

| Situation | Result |
|---|---|
| valid confirmation | 204 |
| valid duplicate notification | 204 |
| valid permanent business rejection | 204 after persisted rejection |
| malformed/invalid signature/topic | 400/403 |
| temporary database/AWS failure | 503 |
| temporary overload | 429 |

## Logging

Log:

- message ID;
- expected/actual topic name or hashed ARN where policy requires;
- notification type;
- video ID;
- processing outcome;
- latency.

Never log:

- Signature;
- Token;
- SubscribeURL;
- S3 POST policy/signature;
- local upload token.

## Availability

SNS pushes directly to this endpoint. Use:

- multiple application instances;
- load balancer health checks;
- HTTPS;
- database idempotency;
- SNS delivery status logging;
- configured retry policy;
- alerting on failed delivery.

## Testing

Create fixture-based tests and a controlled endpoint test. Do not depend only on live SNS during unit testing.
