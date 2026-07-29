namespace Demo.Contracts.Enums;

public enum TranscodeJobStatus
{
    UploadPending = 0,
    Uploading = 1,
    UploadRejected = 2,
    Uploaded = 3,
    Queued = 4,
    Validating = 5,
    Submitted = 6,
    Transcoding = 7,
    Completed = 8,
    Failed = 9,
    Canceled = 10,
    Expired = 11
}
