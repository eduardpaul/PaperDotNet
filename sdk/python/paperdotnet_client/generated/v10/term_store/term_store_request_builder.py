from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .groups.groups_request_builder import GroupsRequestBuilder
    from .keywords.keywords_request_builder import KeywordsRequestBuilder
    from .sets.sets_request_builder import SetsRequestBuilder

class TermStoreRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/termStore
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new TermStoreRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/termStore", path_parameters)
    
    @property
    def groups(self) -> GroupsRequestBuilder:
        """
        The groups property
        """
        from .groups.groups_request_builder import GroupsRequestBuilder

        return GroupsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def keywords(self) -> KeywordsRequestBuilder:
        """
        The keywords property
        """
        from .keywords.keywords_request_builder import KeywordsRequestBuilder

        return KeywordsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def sets(self) -> SetsRequestBuilder:
        """
        The sets property
        """
        from .sets.sets_request_builder import SetsRequestBuilder

        return SetsRequestBuilder(self.request_adapter, self.path_parameters)
    

