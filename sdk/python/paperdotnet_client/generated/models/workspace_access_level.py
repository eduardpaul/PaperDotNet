from enum import Enum

class WorkspaceAccessLevel(str, Enum):
    None_ = "none",
    Read = "read",
    Contribute = "contribute",
    Manage = "manage",

