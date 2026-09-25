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
    from ...models.change_subscription_request import ChangeSubscriptionRequest
    from ...models.change_subscription_response import ChangeSubscriptionResponse
    from ...models.http_validation_problem_details import HttpValidationProblemDetails
    from ...models.page_of_change_subscription_response import PageOfChangeSubscriptionResponse
    from .item.change_subscriptions_item_request_builder import ChangeSubscriptionsItemRequestBuilder

class ChangeSubscriptionsRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/changeSubscriptions
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ChangeSubscriptionsRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/changeSubscriptions", path_parameters)
    
    def by_id(self,id: UUID) -> ChangeSubscriptionsItemRequestBuilder:
        """
        Gets an item from the paperdotnet_client.generated.v10.changeSubscriptions.item collection
        param id: Unique identifier of the item
        Returns: ChangeSubscriptionsItemRequestBuilder
        """
        if id is None:
            raise TypeError("id cannot be null.")
        from .item.change_subscriptions_item_request_builder import ChangeSubscriptionsItemRequestBuilder

        url_tpl_params = get_path_parameters(self.path_parameters)
        url_tpl_params["id"] = id
        return ChangeSubscriptionsItemRequestBuilder(self.request_adapter, url_tpl_params)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[PageOfChangeSubscriptionResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[PageOfChangeSubscriptionResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ...models.page_of_change_subscription_response import PageOfChangeSubscriptionResponse

        return await self.request_adapter.send_async(request_info, PageOfChangeSubscriptionResponse, None)
    
    async def post(self,body: ChangeSubscriptionRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ChangeSubscriptionResponse]:
        """
        param body: Create body. `resource` is `workspaces/{id}/lists/{id}/items` (the whole list) or`…/items/{id}` (one item); `changeTypes` any of `created`, `updated`,`deleted`; `expirationDateTime` at most 30 days ahead (default: the maximum).
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ChangeSubscriptionResponse]
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
        from ...models.change_subscription_response import ChangeSubscriptionResponse

        return await self.request_adapter.send_async(request_info, ChangeSubscriptionResponse, error_mapping)
    
    def to_get_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.GET, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
        request_info.headers.try_add("Accept", "application/json")
        return request_info
    
    def to_post_request_information(self,body: ChangeSubscriptionRequest, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param body: Create body. `resource` is `workspaces/{id}/lists/{id}/items` (the whole list) or`…/items/{id}` (one item); `changeTypes` any of `created`, `updated`,`deleted`; `expirationDateTime` at most 30 days ahead (default: the maximum).
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
    
    def with_url(self,raw_url: str) -> ChangeSubscriptionsRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: ChangeSubscriptionsRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return ChangeSubscriptionsRequestBuilder(self.request_adapter, raw_url)
    
    @dataclass
    class ChangeSubscriptionsRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class ChangeSubscriptionsRequestBuilderPostRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

