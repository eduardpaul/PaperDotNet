from enum import Enum

class TemplateChangeAction(str, Enum):
    Create = "create",
    Update = "update",

