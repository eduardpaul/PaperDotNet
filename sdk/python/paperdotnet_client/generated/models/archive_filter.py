from enum import Enum

class ArchiveFilter(str, Enum):
    Active = "active",
    Archived = "archived",
    All = "all",

