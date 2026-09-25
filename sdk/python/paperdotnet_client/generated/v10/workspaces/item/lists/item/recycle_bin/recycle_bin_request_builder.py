from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.base_request_configuration import RequestConfiguration
from kiota_abstractions.default_query_parameters import QueryParameters
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.method import Method
from kiota_abstractions.request_adapter import RequestAdapter
from kiota_abstractions.request_information import RequestInformation
from kiota_abstractions.request_option import RequestOption
from kiota_abstractions.serialization import Parsable, ParsableFactory
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID
from warnings import warn

if TYPE_CHECKING:
    from .......models.api_problem import ApiProblem
    from .......models.page_of_recycle_bin_item_response import PageOfRecycleBinItemResponse
    from .item.with_item_item_request_builder import WithItemItemRequestBuilder

class RecycleBinRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{workspaceId}/lists/{listId}/recycleBin
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new RecycleBinRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{workspaceId}/lists/{listId}/recycleBin{?%24skiptoken*,%24top*}", path_parameters)
    
    def by_item_id(self,item_id: UUID) -> WithItemItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.lists.item.recycleBin.item collection
        param item_id: Unique identifier of the item
        Returns: WithItemItemRequestBuilder
        """
        if item_id is None:
            raise TypeError("item_id cannot be null.")
        from .item.with_item_item_request_builder import WithItemItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["itemId"] = item_id
        return WithItemItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[RecycleBinRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfRecycleBinItemResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfRecycleBinItemResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from .......models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .......models.page_of_recycle_bin_item_response import PageOfRecycleBinItemResponse

        return await self.request_adapter.send_async(request_info, PageOfRecycleBinItemResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[RecycleBinRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> RecycleBinRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: RecycleBinRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return RecycleBinRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class RecycleBinRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "skiptoken":
                return "%24skiptoken"
            if original_name == "top":
                return "%24top"
            return original_name
        
        # Continuation token from @odata.nextLink.
        skiptoken: Optional[str] = None

        # Page size.
        top: Optional[int] = None

    
    @dataclass
    class RecycleBinRequestBuilderGetRequestConfiguration(RequestConfiguration[RecycleBinRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

