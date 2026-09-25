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
    from .......models.create_item_request import CreateItemRequest
    from .......models.http_validation_problem_details import HttpValidationProblemDetails
    from .......models.item_page import ItemPage
    from .......models.item_response import ItemResponse
    from .bulk_update.bulk_update_request_builder import BulkUpdateRequestBuilder
    from .delta.delta_request_builder import DeltaRequestBuilder
    from .item.with_item_item_request_builder import WithItemItemRequestBuilder

class ItemsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{-id}/lists/{listId}/items
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ItemsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{%2Did}/lists/{listId}/items{?viewId*}", path_parameters)
    
    def by_item_id(self,item_id: UUID) -> WithItemItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.lists.item.items.item collection
        param item_id: Unique identifier of the item
        Returns: WithItemItemRequestBuilder
        """
        if item_id is None:
            raise TypeError("item_id cannot be null.")
        from .item.with_item_item_request_builder import WithItemItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["itemId"] = item_id
        return WithItemItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[ItemsRequestBuilderGetQueryParameters]] = None) -> Optional[ItemPage]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ItemPage]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from .......models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .......models.item_page import ItemPage

        return await self.request_adapter.send_async(request_info, ItemPage, error_mapping)
    
    async def post(self,body: CreateItemRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ItemResponse]:
        """
        param body: Create body: `{ "contentTypeId"?, "parentId"?, "isFolder"?, "fields": { "title": …, … } }`.
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ItemResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from .......models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from .......models.item_response import ItemResponse

        return await self.request_adapter.send_async(request_info, ItemResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[ItemsRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: CreateItemRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: Create body: `{ "contentTypeId"?, "parentId"?, "isFolder"?, "fields": { "title": …, … } }`.
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
    
    @property
    def bulk_update(self) -> BulkUpdateRequestBuilder:
        """
        The bulkUpdate property
        """
        from .bulk_update.bulk_update_request_builder import BulkUpdateRequestBuilder

        return BulkUpdateRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def delta(self) -> DeltaRequestBuilder:
        """
        The delta property
        """
        from .delta.delta_request_builder import DeltaRequestBuilder

        return DeltaRequestBuilder(self.request_adapter, self.path_parameters)
    
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
            if original_name == "view_id":
                return "viewId"
            return original_name
        
        view_id: Optional[UUID] = None

    
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
    

