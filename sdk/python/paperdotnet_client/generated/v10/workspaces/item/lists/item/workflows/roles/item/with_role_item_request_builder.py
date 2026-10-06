from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .runs.runs_request_builder import RunsRequestBuilder

class WithRoleItemRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{workspaceId}/lists/{listId}/workflows/roles/{role}
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new WithRoleItemRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{workspaceId}/lists/{listId}/workflows/roles/{role}", path_parameters)
    
    @property
    def runs(self) -> RunsRequestBuilder:
        """
        The runs property
        """
        from .runs.runs_request_builder import RunsRequestBuilder

        return RunsRequestBuilder(self.request_adapter, self.path_parameters)
    

