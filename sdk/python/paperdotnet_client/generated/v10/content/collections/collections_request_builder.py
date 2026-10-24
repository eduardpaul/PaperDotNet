from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .item.collections_item_request_builder import CollectionsItemRequestBuilder

class CollectionsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/content/collections
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new CollectionsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/content/collections", path_parameters)

    def by_id(self,id: str) -> CollectionsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.content.collections.item collection
        param id: Unique identifier of the item
        Returns: CollectionsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.collections_item_request_builder import CollectionsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return CollectionsItemRequestBuilder(self.request_adapter, url_tpl_params)


