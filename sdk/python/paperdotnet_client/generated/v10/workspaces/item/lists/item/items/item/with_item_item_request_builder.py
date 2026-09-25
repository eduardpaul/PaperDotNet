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
    from ........models.http_validation_problem_details import HttpValidationProblemDetails
    from ........models.item_response import ItemResponse
    from .activity.activity_request_builder import ActivityRequestBuilder
    from .checklist.checklist_request_builder import ChecklistRequestBuilder
    from .children.children_request_builder import ChildrenRequestBuilder
    from .comments.comments_request_builder import CommentsRequestBuilder
    from .file.file_request_builder import FileRequestBuilder
    from .links.links_request_builder import LinksRequestBuilder
    from .permissions.permissions_request_builder import PermissionsRequestBuilder
    from .recurrence.recurrence_request_builder import RecurrenceRequestBuilder
    from .series.series_request_builder import SeriesRequestBuilder
    from .tasks.tasks_request_builder import TasksRequestBuilder
    from .versions.versions_request_builder import VersionsRequestBuilder
    from .workflows.workflows_request_builder import WorkflowsRequestBuilder

class WithItemItemRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{-id}/lists/{listId}/items/{itemId}
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new WithItemItemRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{%2Did}/lists/{listId}/items/{itemId}", path_parameters)
    
    async def delete(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> None:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: None
        """
        request_info = self.to_delete_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        return await self.request_adapter.send_no_response_content_async(request_info, None)
    
    async def get(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ItemResponse]:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ItemResponse]
        """
        request_info = self.to_get_request_information(
            request_configuration
        )
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ........models.item_response import ItemResponse

        return await self.request_adapter.send_async(request_info, ItemResponse, None)
    
    async def patch(self,body: bytes, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> Optional[ItemResponse]:
        """
        param body: The request body
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: Optional[ItemResponse]
        """
        if body is None:
            raise TypeError("body cannot be null.")
        request_info = self.to_patch_request_information(
            body, request_configuration
        )
        from ........models.http_validation_problem_details import HttpValidationProblemDetails

        error_mapping: dict[str, type[ParsableFactory]] = {
            "400": HttpValidationProblemDetails,
        }
        if not self.request_adapter:
            raise Exception("Http core is null") 
        from ........models.item_response import ItemResponse

        return await self.request_adapter.send_async(request_info, ItemResponse, error_mapping)
    
    def to_delete_request_information(self,request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
        """
        param request_configuration: Configuration for the request such as headers, query parameters, and middleware options.
        Returns: RequestInformation
        """
        request_info = RequestInformation(Method.DELETE, self.url_template, self.path_parameters)
        request_info.configure(request_configuration)
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
    
    def to_patch_request_information(self,body: UntypedNode, request_configuration: Optional[RequestConfiguration[QueryParameters]] = None) -> RequestInformation:
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
        request_info.set_content_from_scalar(self.request_adapter, "application/json", body)
        return request_info
    
    def with_url(self,raw_url: str) -> WithItemItemRequestBuilder:
        """
        Returns a request builder with the provided arbitrary URL. Using this method means any other path or query parameters are ignored.
        param raw_url: The raw URL to use for the request builder.
        Returns: WithItemItemRequestBuilder
        """
        if raw_url is None:
            raise TypeError("raw_url cannot be null.")
        return WithItemItemRequestBuilder(self.request_adapter, raw_url)
    
    @property
    def activity(self) -> ActivityRequestBuilder:
        """
        The activity property
        """
        from .activity.activity_request_builder import ActivityRequestBuilder

        return ActivityRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def checklist(self) -> ChecklistRequestBuilder:
        """
        The checklist property
        """
        from .checklist.checklist_request_builder import ChecklistRequestBuilder

        return ChecklistRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def children(self) -> ChildrenRequestBuilder:
        """
        The children property
        """
        from .children.children_request_builder import ChildrenRequestBuilder

        return ChildrenRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def comments(self) -> CommentsRequestBuilder:
        """
        The comments property
        """
        from .comments.comments_request_builder import CommentsRequestBuilder

        return CommentsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def file(self) -> FileRequestBuilder:
        """
        The file property
        """
        from .file.file_request_builder import FileRequestBuilder

        return FileRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def links(self) -> LinksRequestBuilder:
        """
        The links property
        """
        from .links.links_request_builder import LinksRequestBuilder

        return LinksRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def permissions(self) -> PermissionsRequestBuilder:
        """
        The permissions property
        """
        from .permissions.permissions_request_builder import PermissionsRequestBuilder

        return PermissionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def recurrence(self) -> RecurrenceRequestBuilder:
        """
        The recurrence property
        """
        from .recurrence.recurrence_request_builder import RecurrenceRequestBuilder

        return RecurrenceRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def series(self) -> SeriesRequestBuilder:
        """
        The series property
        """
        from .series.series_request_builder import SeriesRequestBuilder

        return SeriesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def tasks(self) -> TasksRequestBuilder:
        """
        The tasks property
        """
        from .tasks.tasks_request_builder import TasksRequestBuilder

        return TasksRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def versions(self) -> VersionsRequestBuilder:
        """
        The versions property
        """
        from .versions.versions_request_builder import VersionsRequestBuilder

        return VersionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def workflows(self) -> WorkflowsRequestBuilder:
        """
        The workflows property
        """
        from .workflows.workflows_request_builder import WorkflowsRequestBuilder

        return WorkflowsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @dataclass
    class WithItemItemRequestBuilderDeleteRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class WithItemItemRequestBuilderGetRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    
    @dataclass
    class WithItemItemRequestBuilderPatchRequestConfiguration(RequestConfiguration[QueryParameters]):
        """
        Configuration for the request such as headers, query parameters, and middleware options.
        """
        warn("This class is deprecated. Please use the generic RequestConfiguration class generated by the generator.", DeprecationWarning)
    

