from __future__ import annotations
import datetime
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .item.with_occurrence_start_item_request_builder import WithOccurrenceStartItemRequestBuilder

class OccurrencesRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{workspaceId}/lists/{listId}/items/{itemId}/series/occurrences
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new OccurrencesRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{workspaceId}/lists/{listId}/items/{itemId}/series/occurrences", path_parameters)
    
    def by_occurrence_start(self,occurrence_start: datetime.datetime) -> WithOccurrenceStartItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.lists.item.items.item.series.occurrences.item collection
        param occurrence_start: Unique identifier of the item
        Returns: WithOccurrenceStartItemRequestBuilder
        """
        if occurrence_start is None:
            raise TypeError("occurrence_start cannot be null.")
        from .item.with_occurrence_start_item_request_builder import WithOccurrenceStartItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["occurrenceStart"] = occurrence_start
        return WithOccurrenceStartItemRequestBuilder(self.request_adapter, url_tpl_params)
    

