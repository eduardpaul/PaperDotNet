from enum import Enum

class AclPrincipalType(str, Enum):
    User = "user",
    Group = "group",
    WorkspaceVisitors = "workspaceVisitors",
    WorkspaceMembers = "workspaceMembers",
    WorkspaceOwners = "workspaceOwners",

