from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .item.with_role_item_request_builder import WithRoleItemRequestBuilder

class RolesRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{workspaceId}/lists/{listId}/workflows/roles
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new RolesRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{workspaceId}/lists/{listId}/workflows/roles", path_parameters)
    
    def by_role(self,role: str) -> WithRoleItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.lists.item.workflows.roles.item collection
        param role: Unique identifier of the item
        Returns: WithRoleItemRequestBuilder
        """
        if role is None:
            raise TypeError("role cannot be null.")
        from .item.with_role_item_request_builder import WithRoleItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["role"] = role
        return WithRoleItemRequestBuilder(self.request_adapter, url_tpl_params)
    

