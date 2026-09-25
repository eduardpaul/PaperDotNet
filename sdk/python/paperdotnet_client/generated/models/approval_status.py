from enum import Enum

class ApprovalStatus(str, Enum):
    Pending = "pending",
    Approved = "approved",
    Rejected = "rejected",
    Cancelled = "cancelled",

