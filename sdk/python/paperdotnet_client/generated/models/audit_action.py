from enum import Enum

class AuditAction(str, Enum):
    Created = "created",
    Updated = "updated",
    Deleted = "deleted",
    Restored = "restored",
    Purged = "purged",

