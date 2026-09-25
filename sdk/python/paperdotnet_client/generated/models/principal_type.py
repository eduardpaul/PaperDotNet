from enum import Enum

class PrincipalType(str, Enum):
    User = "user",
    Group = "group",

