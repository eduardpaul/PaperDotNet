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
from warnings import warn

if TYPE_CHECKING:
    from ..........models.api_problem import ApiProblem
    from ..........models.edit_pages_request import EditPagesRequest
    from ..........models.file_version_response import FileVersionResponse
    from .extract.extract_request_builder import ExtractRequestBuilder
    from .item.with_page_item_request_builder import WithPageItemRequestBuilder
    from .move.move_request_builder import MoveRequestBuilder

class PagesRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{-id}/lists/{listId}/items/{itemId}/file/pages
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new PagesRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{%2Did}/lists/{listId}/items/{itemId}/file/pages", path_parameters)
    
    def by_page(self,page: int) -> WithPageItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.workspaces.item.lists.item.items.item.file.pages.item collection
        param page: Unique identifier of the item
        Returns: WithPageItemRequestBuilder
        """
        if page is None:
            raise TypeError("page cannot be null.")
        from .item.with_page_item_request_builder import WithPageItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["page"] = page
        return WithPageItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def put(self,body: EditPagesRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[FileVersionResponse]:
        """
        param body: The pages of the new version in order: pages left out are deleted (DOC-05).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[FileVersionResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_put_request_information(
            body, request_configuration
        )
        from ..........models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ..........models.file_version_response import FileVersionResponse

        return await self.request_adapter.send_async(request_info, FileVersionResponse, error_mapping)
    
    def to_put_request_information(self,body: EditPagesRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: The pages of the new version in order: pages left out are deleted (DOC-05).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = RequestInformation(Method.PUT, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        request_info.set_content_from_parsable(self.request_adapter, "application/json", body)
        return request_info
    
    def with_url(self,raw_url: str) -> PagesRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: PagesRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return PagesRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def extract(self) -> ExtractRequestBuilder:
        """
        The extract property
        """
        from .extract.extract_request_builder import ExtractRequestBuilder

        return ExtractRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def move(self) -> MoveRequestBuilder:
        """
        The move property
        """
        from .move.move_request_builder import MoveRequestBuilder

        return MoveRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class PagesRequestBuilderPutRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

