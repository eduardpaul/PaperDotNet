from enum import Enum

class OperationStatus(str, Enum):
    NotStarted = "notStarted",
    Running = "running",
    Succeeded = "succeeded",
    Failed = "failed",

