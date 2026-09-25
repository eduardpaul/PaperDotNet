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
    from ....models.passkey_registration_request import PasskeyRegistrationRequest
    from ....models.passkey_response import PasskeyResponse
    from .item.passkeys_item_request_builder import PasskeysItemRequestBuilder
    from .options.options_request_builder import OptionsRequestBuilder

class PasskeysRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/me/passkeys
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new PasskeysRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/me/passkeys", path_parameters)
    
    def by_id(self,id: str) -> PasskeysItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.me.passkeys.item collection
        param id: Unique identifier of the item
        Returns: PasskeysItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.passkeys_item_request_builder import PasskeysItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return PasskeysItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[list[PasskeyResponse]]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[list[PasskeyResponse]]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.passkey_response import PasskeyResponse

        return await self.request_adapter.send_collection_async(request_info, PasskeyResponse, None)
    
    async def post(self,body: PasskeyRegistrationRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[PasskeyResponse]:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PasskeyResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_post_request_information(
            body, request_configuration
        )
        from ....models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ....models.passkey_response import PasskeyResponse

        return await self.request_adapter.send_async(request_info, PasskeyResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: PasskeyRegistrationRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: The request body
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
    
    def with_url(self,raw_url: str) -> PasskeysRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: PasskeysRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return PasskeysRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def options_path(self) -> OptionsRequestBuilder:
        """
        The optionsPath property
        """
        from .options.options_request_builder import OptionsRequestBuilder

        return OptionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class PasskeysRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class PasskeysRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

