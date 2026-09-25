from enum import Enum

class ProcessingStatus(str, Enum):
    None_ = "none",
    Scheduled = "scheduled",
    Running = "running",
    Succeeded = "succeeded",
    Failed = "failed",

