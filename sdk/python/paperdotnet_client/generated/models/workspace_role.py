from enum import Enum

class WorkspaceRole(str, Enum):
    Member = "member",
    Owner = "owner",
    Visitor = "visitor",

