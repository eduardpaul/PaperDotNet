from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .item.with_list_item_request_builder import WithListItemRequestBuilder

class ListsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/ext/samples.invoices/workspaces/{workspaceId}/lists
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ListsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/ext/samples.invoices/workspaces/{workspaceId}/lists", path_parameters)
    
    def by_list_id(self,list_id: UUID) -> WithListItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.ext.samplesInvoices.workspaces.item.lists.item collection
        param list_id: Unique identifier of the item
        Returns: WithListItemRequestBuilder
        """
        if list_id is None:
            raise TypeError("list_id cannot be null.")
        from .item.with_list_item_request_builder import WithListItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["listId"] = list_id
        return WithListItemRequestBuilder(self.request_adapter, url_tpl_params)
    

