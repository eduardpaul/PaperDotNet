from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .rules.rules_request_builder import RulesRequestBuilder
    from .runs.runs_request_builder import RunsRequestBuilder
    from .workflows.workflows_request_builder import WorkflowsRequestBuilder

class AutomationRequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0/workspaces/{-id}/automation
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new AutomationRequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0/workspaces/{%2Did}/automation", path_parameters)
    
    @property
    def rules(self) -> RulesRequestBuilder:
        """
        The rules property
        """
        from .rules.rules_request_builder import RulesRequestBuilder

        return RulesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def runs(self) -> RunsRequestBuilder:
        """
        The runs property
        """
        from .runs.runs_request_builder import RunsRequestBuilder

        return RunsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def workflows(self) -> WorkflowsRequestBuilder:
        """
        The workflows property
        """
        from .workflows.workflows_request_builder import WorkflowsRequestBuilder

        return WorkflowsRequestBuilder(self.request_adapter, self.path_parameters)
    

