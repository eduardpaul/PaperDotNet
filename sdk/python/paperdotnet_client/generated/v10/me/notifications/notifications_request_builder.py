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
    from ....models.api_problem import ApiProblem
    from ....models.page_of_notification_response import PageOfNotificationResponse
    from .item.notifications_item_request_builder import NotificationsItemRequestBuilder
    from .read.read_request_builder import ReadRequestBuilder
    from .unread_count.unread_count_request_builder import UnreadCountRequestBuilder

class NotificationsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/me/notifications
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new NotificationsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/me/notifications{?%24skiptoken*,%24top*,unreadOnly*}", path_parameters)
    
    def by_id(self,id: UUID) -> NotificationsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.me.notifications.item collection
        param id: Unique identifier of the item
        Returns: NotificationsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.notifications_item_request_builder import NotificationsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return NotificationsItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[NotificationsRequestBuilderGetQueryParameters]] = None) -> Optional[PageOfNotificationResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfNotificationResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ....models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.page_of_notification_response import PageOfNotificationResponse

        return await self.request_adapter.send_async(request_info, PageOfNotificationResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[NotificationsRequestBuilderGetQueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> NotificationsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: NotificationsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return NotificationsRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def read(self) -> ReadRequestBuilder:
        """
        The read property
        """
        from .read.read_request_builder import ReadRequestBuilder

        return ReadRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def unread_count(self) -> UnreadCountRequestBuilder:
        """
        The unreadCount property
        """
        from .unread_count.unread_count_request_builder import UnreadCountRequestBuilder

        return UnreadCountRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class NotificationsRequestBuilderGetQueryParameters():
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
            if original_name == "unread_only":
                return "unreadOnly"
            return original_name
        
        # Continuation token from @odata.nextLink.
        skiptoken: Optional[str] = None

        # Page size.
        top: Optional[int] = None

        unread_only: Optional[bool] = None

    
    @dataclass
    class NotificationsRequestBuilderGetRequestConfiguration(RequestConfiguration[NotificationsRequestBuilderGetQueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

