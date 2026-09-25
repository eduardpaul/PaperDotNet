from enum import Enum

class DuplicatePolicy(str, Enum):
    Allow = "allow",
    Warn = "warn",
    Block = "block",

