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
    from ...models.application_response import ApplicationResponse
    from ...models.application_secret_response import ApplicationSecretResponse
    from ...models.create_application_request import CreateApplicationRequest
    from ...models.http_validation_problem_details import HttpValidationProblemDetails
    from .item.applications_item_request_builder import ApplicationsItemRequestBuilder

class ApplicationsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/applications
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ApplicationsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/applications", path_parameters)
    
    def by_id(self,id: UUID) -> ApplicationsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.applications.item collection
        param id: Unique identifier of the item
        Returns: ApplicationsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.applications_item_request_builder import ApplicationsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return ApplicationsItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[list[ApplicationResponse]]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[list[ApplicationResponse]]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.application_response import ApplicationResponse

        return await self.request_adapter.send_collection_async(request_info, ApplicationResponse, None)
    
    async def post(self,body: CreateApplicationRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ApplicationSecretResponse]:
        """
        param body: A new OAuth client. `clientType`: `confidential` (has a secret) or `public`(native/SPA, PKCE only). `grantTypes`: `authorization_code`, `refresh_token`,`client_credentials` (confidential only; the client acts as its own service account).`scopes`: permission scopes, or `api` for full access as the user.
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ApplicationSecretResponse]
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
        from ...models.application_secret_response import ApplicationSecretResponse

        return await self.request_adapter.send_async(request_info, ApplicationSecretResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: CreateApplicationRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: A new OAuth client. `clientType`: `confidential` (has a secret) or `public`(native/SPA, PKCE only). `grantTypes`: `authorization_code`, `refresh_token`,`client_credentials` (confidential only; the client acts as its own service account).`scopes`: permission scopes, or `api` for full access as the user.
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
    
    def with_url(self,raw_url: str) -> ApplicationsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: ApplicationsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return ApplicationsRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class ApplicationsRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class ApplicationsRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

