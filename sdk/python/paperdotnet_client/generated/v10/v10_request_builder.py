from __future__ import annotations
from collections.abc import Callable
from kiota_abstractions.base_request_builder import BaseRequestBuilder
from kiota_abstractions.get_path_parameters import get_path_parameters
from kiota_abstractions.request_adapter import RequestAdapter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .applications.applications_request_builder import ApplicationsRequestBuilder
    from .audit_log.audit_log_request_builder import AuditLogRequestBuilder
    from .auth.auth_request_builder import AuthRequestBuilder
    from .automation.automation_request_builder import AutomationRequestBuilder
    from .batch.batch_request_builder import BatchRequestBuilder
    from .calendar_feeds.calendar_feeds_request_builder import CalendarFeedsRequestBuilder
    from .change_subscriptions.change_subscriptions_request_builder import ChangeSubscriptionsRequestBuilder
    from .content_types.content_types_request_builder import ContentTypesRequestBuilder
    from .ext.ext_request_builder import ExtRequestBuilder
    from .extensions.extensions_request_builder import ExtensionsRequestBuilder
    from .field_types.field_types_request_builder import FieldTypesRequestBuilder
    from .groups.groups_request_builder import GroupsRequestBuilder
    from .list_templates.list_templates_request_builder import ListTemplatesRequestBuilder
    from .me.me_request_builder import MeRequestBuilder
    from .operations.operations_request_builder import OperationsRequestBuilder
    from .organization.organization_request_builder import OrganizationRequestBuilder
    from .provisioning.provisioning_request_builder import ProvisioningRequestBuilder
    from .roles.roles_request_builder import RolesRequestBuilder
    from .scopes.scopes_request_builder import ScopesRequestBuilder
    from .search.search_request_builder import SearchRequestBuilder
    from .term_store.term_store_request_builder import TermStoreRequestBuilder
    from .users.users_request_builder import UsersRequestBuilder
    from .workspaces.workspaces_request_builder import WorkspacesRequestBuilder

class V10RequestBuilder(BaseRequestBuilder):
    """
    Builds and executes requests for operations under /v1.0
    """
    def __init__(self,request_adapter: RequestAdapter, path_parameters: Union[str, dict[str, Any]]) -> None:
        """
        Instantiates a new V10RequestBuilder and sets the default values.
        param path_parameters: The raw url or the url-template parameters for the request.
        param request_adapter: The request adapter to use to execute the requests.
        Returns: None
        """
        super().__init__(request_adapter, "{+baseurl}/v1.0", path_parameters)
    
    @property
    def applications(self) -> ApplicationsRequestBuilder:
        """
        The applications property
        """
        from .applications.applications_request_builder import ApplicationsRequestBuilder

        return ApplicationsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def audit_log(self) -> AuditLogRequestBuilder:
        """
        The auditLog property
        """
        from .audit_log.audit_log_request_builder import AuditLogRequestBuilder

        return AuditLogRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def auth(self) -> AuthRequestBuilder:
        """
        The auth property
        """
        from .auth.auth_request_builder import AuthRequestBuilder

        return AuthRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def automation(self) -> AutomationRequestBuilder:
        """
        The automation property
        """
        from .automation.automation_request_builder import AutomationRequestBuilder

        return AutomationRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def batch(self) -> BatchRequestBuilder:
        """
        The Batch property
        """
        from .batch.batch_request_builder import BatchRequestBuilder

        return BatchRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def calendar_feeds(self) -> CalendarFeedsRequestBuilder:
        """
        The calendarFeeds property
        """
        from .calendar_feeds.calendar_feeds_request_builder import CalendarFeedsRequestBuilder

        return CalendarFeedsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def change_subscriptions(self) -> ChangeSubscriptionsRequestBuilder:
        """
        The changeSubscriptions property
        """
        from .change_subscriptions.change_subscriptions_request_builder import ChangeSubscriptionsRequestBuilder

        return ChangeSubscriptionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def content_types(self) -> ContentTypesRequestBuilder:
        """
        The contentTypes property
        """
        from .content_types.content_types_request_builder import ContentTypesRequestBuilder

        return ContentTypesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def ext(self) -> ExtRequestBuilder:
        """
        The ext property
        """
        from .ext.ext_request_builder import ExtRequestBuilder

        return ExtRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def extensions(self) -> ExtensionsRequestBuilder:
        """
        The extensions property
        """
        from .extensions.extensions_request_builder import ExtensionsRequestBuilder

        return ExtensionsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def field_types(self) -> FieldTypesRequestBuilder:
        """
        The fieldTypes property
        """
        from .field_types.field_types_request_builder import FieldTypesRequestBuilder

        return FieldTypesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def groups(self) -> GroupsRequestBuilder:
        """
        The groups property
        """
        from .groups.groups_request_builder import GroupsRequestBuilder

        return GroupsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def list_templates(self) -> ListTemplatesRequestBuilder:
        """
        The listTemplates property
        """
        from .list_templates.list_templates_request_builder import ListTemplatesRequestBuilder

        return ListTemplatesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def me(self) -> MeRequestBuilder:
        """
        The me property
        """
        from .me.me_request_builder import MeRequestBuilder

        return MeRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def operations(self) -> OperationsRequestBuilder:
        """
        The operations property
        """
        from .operations.operations_request_builder import OperationsRequestBuilder

        return OperationsRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def organization(self) -> OrganizationRequestBuilder:
        """
        The organization property
        """
        from .organization.organization_request_builder import OrganizationRequestBuilder

        return OrganizationRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def provisioning(self) -> ProvisioningRequestBuilder:
        """
        The provisioning property
        """
        from .provisioning.provisioning_request_builder import ProvisioningRequestBuilder

        return ProvisioningRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def roles(self) -> RolesRequestBuilder:
        """
        The roles property
        """
        from .roles.roles_request_builder import RolesRequestBuilder

        return RolesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def scopes(self) -> ScopesRequestBuilder:
        """
        The scopes property
        """
        from .scopes.scopes_request_builder import ScopesRequestBuilder

        return ScopesRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def search(self) -> SearchRequestBuilder:
        """
        The search property
        """
        from .search.search_request_builder import SearchRequestBuilder

        return SearchRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def term_store(self) -> TermStoreRequestBuilder:
        """
        The termStore property
        """
        from .term_store.term_store_request_builder import TermStoreRequestBuilder

        return TermStoreRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def users(self) -> UsersRequestBuilder:
        """
        The users property
        """
        from .users.users_request_builder import UsersRequestBuilder

        return UsersRequestBuilder(self.request_adapter, self.path_parameters)
    
    @property
    def workspaces(self) -> WorkspacesRequestBuilder:
        """
        The workspaces property
        """
        from .workspaces.workspaces_request_builder import WorkspacesRequestBuilder

        return WorkspacesRequestBuilder(self.request_adapter, self.path_parameters)
    

