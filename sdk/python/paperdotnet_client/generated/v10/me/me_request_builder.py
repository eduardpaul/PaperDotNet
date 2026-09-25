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
    from ...models.api_problem import ApiProblem
    from ...models.me_response import MeResponse
    from .api_tokens.api_tokens_request_builder import ApiTokensRequestBuilder
    from .approvals.approvals_request_builder import ApprovalsRequestBuilder
    from .calendar.calendar_request_builder import CalendarRequestBuilder
    from .calendar_feeds.calendar_feeds_request_builder import CalendarFeedsRequestBuilder
    from .events.events_request_builder import EventsRequestBuilder
    from .home.home_request_builder import HomeRequestBuilder
    from .inbox.inbox_request_builder import InboxRequestBuilder
    from .inboxes.inboxes_request_builder import InboxesRequestBuilder
    from .notifications.notifications_request_builder import NotificationsRequestBuilder
    from .notification_settings.notification_settings_request_builder import NotificationSettingsRequestBuilder
    from .passkeys.passkeys_request_builder import PasskeysRequestBuilder
    from .password.password_request_builder import PasswordRequestBuilder
    from .preferences.preferences_request_builder import PreferencesRequestBuilder
    from .subscriptions.subscriptions_request_builder import SubscriptionsRequestBuilder
    from .tasks.tasks_request_builder import TasksRequestBuilder

class MeRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/me
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new MeRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/me", path_parameters)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[MeResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[MeResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        from ...models.api_problem import ApiProblem

        error_mapping: dict[str, type[ParsableFactory]] = {
            "XXX": ApiProblem,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.me_response import MeResponse

        return await self.request_adapter.send_async(request_info, MeResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def with_url(self,raw_url: str) -> MeRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: MeRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return MeRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def api_tokens(self) -> ApiTokensRequestBuilder:
        """
        The apiTokens property
        """
        from .api_tokens.api_tokens_request_builder import ApiTokensRequestBuilder

        return ApiTokensRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def approvals(self) -> ApprovalsRequestBuilder:
        """
        The approvals property
        """
        from .approvals.approvals_request_builder import ApprovalsRequestBuilder

        return ApprovalsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def calendar(self) -> CalendarRequestBuilder:
        """
        The calendar property
        """
        from .calendar.calendar_request_builder import CalendarRequestBuilder

        return CalendarRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def calendar_feeds(self) -> CalendarFeedsRequestBuilder:
        """
        The calendarFeeds property
        """
        from .calendar_feeds.calendar_feeds_request_builder import CalendarFeedsRequestBuilder

        return CalendarFeedsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def events(self) -> EventsRequestBuilder:
        """
        The events property
        """
        from .events.events_request_builder import EventsRequestBuilder

        return EventsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def home(self) -> HomeRequestBuilder:
        """
        The home property
        """
        from .home.home_request_builder import HomeRequestBuilder

        return HomeRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def inbox(self) -> InboxRequestBuilder:
        """
        The inbox property
        """
        from .inbox.inbox_request_builder import InboxRequestBuilder

        return InboxRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def inboxes(self) -> InboxesRequestBuilder:
        """
        The inboxes property
        """
        from .inboxes.inboxes_request_builder import InboxesRequestBuilder

        return InboxesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def notification_settings(self) -> NotificationSettingsRequestBuilder:
        """
        The notificationSettings property
        """
        from .notification_settings.notification_settings_request_builder import NotificationSettingsRequestBuilder

        return NotificationSettingsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def notifications(self) -> NotificationsRequestBuilder:
        """
        The notifications property
        """
        from .notifications.notifications_request_builder import NotificationsRequestBuilder

        return NotificationsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def passkeys(self) -> PasskeysRequestBuilder:
        """
        The passkeys property
        """
        from .passkeys.passkeys_request_builder import PasskeysRequestBuilder

        return PasskeysRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def password(self) -> PasswordRequestBuilder:
        """
        The password property
        """
        from .password.password_request_builder import PasswordRequestBuilder

        return PasswordRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def preferences(self) -> PreferencesRequestBuilder:
        """
        The preferences property
        """
        from .preferences.preferences_request_builder import PreferencesRequestBuilder

        return PreferencesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def subscriptions(self) -> SubscriptionsRequestBuilder:
        """
        The subscriptions property
        """
        from .subscriptions.subscriptions_request_builder import SubscriptionsRequestBuilder

        return SubscriptionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def tasks(self) -> TasksRequestBuilder:
        """
        The tasks property
        """
        from .tasks.tasks_request_builder import TasksRequestBuilder

        return TasksRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class MeRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

