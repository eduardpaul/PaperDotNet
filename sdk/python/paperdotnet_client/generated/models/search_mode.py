from enum import Enum

class SearchMode(str, Enum):
    Keyword = "keyword",
    Semantic = "semantic",
    Hybrid = "hybrid",

