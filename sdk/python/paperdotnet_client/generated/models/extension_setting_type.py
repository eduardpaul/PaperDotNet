from enum import Enum

class ExtensionSettingType(str, Enum):
    Text = "text",
    Number = "number",
    Boolean = "boolean",
    Choice = "choice",

