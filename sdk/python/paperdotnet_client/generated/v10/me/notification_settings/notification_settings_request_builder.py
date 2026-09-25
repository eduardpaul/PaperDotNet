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
    from ....models.http_validation_problem_details import HttpValidationProblemDetails
    from ....models.settings_request import SettingsRequest
    from ....models.settings_response import SettingsResponse
    from .test.test_request_builder import TestRequestBuilder
    from .webhook_secret.webhook_secret_request_builder import WebhookSecretRequestBuilder

class NotificationSettingsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/me/notificationSettings
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new NotificationSettingsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/me/notificationSettings", path_parameters)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[SettingsResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[SettingsResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.settings_response import SettingsResponse

        return await self.request_adapter.send_async(request_info, SettingsResponse, None)
    
    async def put(self,body: SettingsRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[SettingsResponse]:
        """
        param body: Replaces the settings. Channels not listed use the defaults (in-app and webhook on).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[SettingsResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_put_request_information(
            body, request_configuration
        )
        from ....models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.settings_response import SettingsResponse

        return await self.request_adapter.send_async(request_info, SettingsResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_put_request_information(self,body: SettingsRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: Replaces the settings. Channels not listed use the defaults (in-app and webhook on).
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
    
    def with_url(self,raw_url: str) -> NotificationSettingsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: NotificationSettingsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return NotificationSettingsRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def test(self) -> TestRequestBuilder:
        """
        The test property
        """
        from .test.test_request_builder import TestRequestBuilder

        return TestRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def webhook_secret(self) -> WebhookSecretRequestBuilder:
        """
        The webhookSecret property
        """
        from .webhook_secret.webhook_secret_request_builder import WebhookSecretRequestBuilder

        return WebhookSecretRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class NotificationSettingsRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class NotificationSettingsRequestBuilderPutRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

