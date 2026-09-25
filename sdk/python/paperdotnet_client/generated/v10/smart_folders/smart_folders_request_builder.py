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
    from ...models.http_validation_problem_details import HttpValidationProblemDetails
    from ...models.page_of_smart_folder_response import PageOfSmartFolderResponse
    from ...models.smart_folder_request import SmartFolderRequest
    from ...models.smart_folder_response import SmartFolderResponse
    from .item.smart_folders_item_request_builder import SmartFoldersItemRequestBuilder

class SmartFoldersRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/smartFolders
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new SmartFoldersRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/smartFolders{?workspaceId*}", path_parameters)
    
    def by_id(self,id: UUID) -> SmartFoldersItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.smartFolders.item collection
        param id: Unique identifier of the item
        Returns: SmartFoldersItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.smart_folders_item_request_builder import SmartFoldersItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return SmartFoldersItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[SmartFoldersRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfSmartFolderResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfSmartFolderResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.page_of_smart_folder_response import PageOfSmartFolderResponse

        return await self.request_adapter.send_async(request_info, PageOfSmartFolderResponse, None)
    
    async def post(self,body: SmartFolderRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[SmartFolderResponse]:
        """
        param body: Create/update body. `personal` folders belong to the caller; others to `workspaceId` (Manage needed).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[SmartFolderResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from ...models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.smart_folder_response import SmartFolderResponse

        return await self.request_adapter.send_async(request_info, SmartFolderResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[SmartFoldersRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: SmartFolderRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: Create/update body. `personal` folders belong to the caller; others to `workspaceId` (Manage needed).
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
    
    def with_url(self,raw_url: str) -> SmartFoldersRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: SmartFoldersRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return SmartFoldersRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class SmartFoldersRequestBuilderGetQueryParameters():
        def get_query_parameter(self,original_name: str) -> str:
            """
            Maps the query parameters names to their encoded names for the URI template parsing.
            param original_name: The original query parameter name in the class.
            Returns: str
            """
            if original_name is None:
                raise TypeError("original_name cannot be null.")
            if original_name == "workspace_id":
                return "workspaceId"
            return original_name
        
        workspace_id: Optional[UUID] = None

    
    @dataclass
    class SmartFoldersRequestBuilderGetRequestConfiguration(RequestConfiguration[SmartFoldersRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class SmartFoldersRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

