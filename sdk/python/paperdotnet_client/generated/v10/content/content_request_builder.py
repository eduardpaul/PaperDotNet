from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .collections.collections_request_builder import CollectionsRequestBuilder
    from .items.items_request_builder import ItemsRequestBuilder
    from .migrations.migrations_request_builder import MigrationsRequestBuilder
    from .resources.resources_request_builder import ResourcesRequestBuilder
    from .workspaces.workspaces_request_builder import WorkspacesRequestBuilder

class ContentRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/content
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new ContentRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/content", path_parameters)

    @property
    def collections(self) -> CollectionsRequestBuilder:
        """
        The collections property
        """
        from .collections.collections_request_builder import CollectionsRequestBuilder

        return CollectionsRequestBuilder(self.request_adapter, self.path_parameters)

    @property
    def items(self) -> ItemsRequestBuilder:
        """
        The items property
        """
        from .items.items_request_builder import ItemsRequestBuilder

        return ItemsRequestBuilder(self.request_adapter, self.path_parameters)

    @property
    def migrations(self) -> MigrationsRequestBuilder:
        """
        The migrations property
        """
        from .migrations.migrations_request_builder import MigrationsRequestBuilder

        return MigrationsRequestBuilder(self.request_adapter, self.path_parameters)

    @property
    def resources(self) -> ResourcesRequestBuilder:
        """
        The resources property
        """
        from .resources.resources_request_builder import ResourcesRequestBuilder

        return ResourcesRequestBuilder(self.request_adapter, self.path_parameters)

    @property
    def workspaces(self) -> WorkspacesRequestBuilder:
        """
        The workspaces property
        """
        from .workspaces.workspaces_request_builder import WorkspacesRequestBuilder

        return WorkspacesRequestBuilder(self.request_adapter, self.path_parameters)


