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
    from ......models.api_problem import ApiProblem
    from ......models.list_response import ListResponse
    from ......models.update_list_request import UpdateListRequest
    from .calendar.calendar_request_builder import CalendarRequestBuilder
    from .calendar_ics.calendar_ics_request_builder import CalendarIcsRequestBuilder
    from .content_types.content_types_request_builder import ContentTypesRequestBuilder
    from .documents.documents_request_builder import DocumentsRequestBuilder
    from .document_settings.document_settings_request_builder import DocumentSettingsRequestBuilder
    from .items.items_request_builder import ItemsRequestBuilder
    from .permissions.permissions_request_builder import PermissionsRequestBuilder
    from .recycle_bin.recycle_bin_request_builder import RecycleBinRequestBuilder
    from .views.views_request_builder import ViewsRequestBuilder

class WithListItemRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{workspaceId}/lists/{listId}
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new WithListItemRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{workspaceId}/lists/{listId}", path_parameters)
    
    async def delete(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> None:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: None
        """
        request_info = self.to_delete_request_information(
            request_configuration
        )
        from ......models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        return await self.request_adapter.send_no_response_content_async(request_info, error_mapping)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ListResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ListResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ......models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ......models.list_response import ListResponse

        return await self.request_adapter.send_async(request_info, ListResponse, error_mapping)
    
    async def patch(self,body: UpdateListRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ListResponse]:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ListResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_patch_request_information(
            body, request_configuration
        )
        from ......models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": ApiProblem,
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ......models.list_response import ListResponse

        return await self.request_adapter.send_async(request_info, ListResponse, error_mapping)
    
    def to_delete_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.DELETE, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/problem+json")
        return request_info
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_patch_request_information(self,body: UpdateListRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = RequestInformation(Method.PATCH, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        request_info.set_content_from_parsable(self.request_adapter, "application/json", body)
        return request_info
    
    def with_url(self,raw_url: str) -> WithListItemRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: WithListItemRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return WithListItemRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def calendar(self) -> CalendarRequestBuilder:
        """
        The calendar property
        """
        from .calendar.calendar_request_builder import CalendarRequestBuilder

        return CalendarRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def calendar_ics(self) -> CalendarIcsRequestBuilder:
        """
        The calendarIcs property
        """
        from .calendar_ics.calendar_ics_request_builder import CalendarIcsRequestBuilder

        return CalendarIcsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def content_types(self) -> ContentTypesRequestBuilder:
        """
        The contentTypes property
        """
        from .content_types.content_types_request_builder import ContentTypesRequestBuilder

        return ContentTypesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def document_settings(self) -> DocumentSettingsRequestBuilder:
        """
        The documentSettings property
        """
        from .document_settings.document_settings_request_builder import DocumentSettingsRequestBuilder

        return DocumentSettingsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def documents(self) -> DocumentsRequestBuilder:
        """
        The documents property
        """
        from .documents.documents_request_builder import DocumentsRequestBuilder

        return DocumentsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def items(self) -> ItemsRequestBuilder:
        """
        The items property
        """
        from .items.items_request_builder import ItemsRequestBuilder

        return ItemsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def permissions(self) -> PermissionsRequestBuilder:
        """
        The permissions property
        """
        from .permissions.permissions_request_builder import PermissionsRequestBuilder

        return PermissionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def recycle_bin(self) -> RecycleBinRequestBuilder:
        """
        The recycleBin property
        """
        from .recycle_bin.recycle_bin_request_builder import RecycleBinRequestBuilder

        return RecycleBinRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def views(self) -> ViewsRequestBuilder:
        """
        The views property
        """
        from .views.views_request_builder import ViewsRequestBuilder

        return ViewsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class WithListItemRequestBuilderDeleteRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class WithListItemRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class WithListItemRequestBuilderPatchRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

