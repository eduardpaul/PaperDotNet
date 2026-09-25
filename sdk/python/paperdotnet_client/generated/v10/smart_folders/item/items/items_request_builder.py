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
    from .....models.api_problem import ApiProblem
    from .....models.page_of_smart_folder_entry import PageOfSmartFolderEntry
    from .....models.smart_folder_drop_request import SmartFolderDropRequest
    from .....models.smart_folder_entry import SmartFolderEntry
    from .item.with_item_item_request_builder import WithItemItemRequestBuilder

class ItemsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/smartFolders/{id}/items
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ItemsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/smartFolders/{id}/items{?%24skiptoken*,%24top*,path*}", path_parameters)
    
    def by_item_id(self,item_id: UUID) -> WithItemItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.smartFolders.item.items.item collection
        param item_id: Unique identifier of the item
        Returns: WithItemItemRequestBuilder
        """
        if item_id is None:
            raise TypeError("item_id cannot be null.")
        from .item.with_item_item_request_builder import WithItemItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["itemId"] = item_id
        return WithItemItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[ItemsRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfSmartFolderEntry]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfSmartFolderEntry]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from .....models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .....models.page_of_smart_folder_entry import PageOfSmartFolderEntry

        return await self.request_adapter.send_async(request_info, PageOfSmartFolderEntry, error_mapping)
    
    async def post(self,body: SmartFolderDropRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[SmartFolderEntry]:
        """
        param body: Drop to classify (TAX-09): an existing item (`itemId`) gets the folder's terms and the values of its`eq` conditions (and of the sub-folder `path`); or a new item is created with them (`fields`).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[SmartFolderEntry]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from .....models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .....models.smart_folder_entry import SmartFolderEntry

        return await self.request_adapter.send_async(request_info, SmartFolderEntry, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[ItemsRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: SmartFolderDropRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: Drop to classify (TAX-09): an existing item (`itemId`) gets the folder's terms and the values of its`eq` conditions (and of the sub-folder `path`); or a new item is created with them (`fields`).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = RequestInformation(Method.POST, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        request_info.set_content_from_parsable(self.request_adapter, "application/json", body)
        return request_info
    
    def with_url(self,raw_url: str) -> ItemsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: ItemsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return ItemsRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class ItemsRequestBuilderGetQueryParameters():
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
            if original_name == "path":
                return "path"
            return original_name
        
        path: Optional[list[str]] = None

        # Continuation token from @odata.nextLink.
        skiptoken: Optional[str] = None

        # Page size.
        top: Optional[int] = None

    
    @dataclass
    class ItemsRequestBuilderGetRequestConfiguration(RequestConfiguration[ItemsRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class ItemsRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

