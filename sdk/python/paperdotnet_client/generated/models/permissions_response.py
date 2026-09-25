from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .permission_grant_dto import PermissionGrantDto
    from .workspace_access_level import WorkspaceAccessLevel

@dataclass
class PermissionsResponse(AdditionalDataHolder, Parsable):
    """
    Permissions of a list or item. `inheritsFrom` names where they come from:`workspace`, `list` or `item` (with `inheritsFromId`), or null when unique.Grants are shown to managers only.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # What the current user may do in a workspace. Ordered: higher includes lower.
    effective_level: Optional[WorkspaceAccessLevel] = None
    # The grants property
    grants: Optional[list[PermissionGrantDto]] = None
    # The hasUniquePermissions property
    has_unique_permissions: Optional[bool] = None
    # The inheritsFrom property
    inherits_from: Optional[str] = None
    # The inheritsFromId property
    inherits_from_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> PermissionsResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: PermissionsResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return PermissionsResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .permission_grant_dto import PermissionGrantDto
        from .workspace_access_level import WorkspaceAccessLevel

        from .permission_grant_dto import PermissionGrantDto
        from .workspace_access_level import WorkspaceAccessLevel

        fields: dict[str, Callable[[Any], None]] = {
            "effectiveLevel": lambda n : setattr(self, 'effective_level', n.get_enum_value(WorkspaceAccessLevel)),
            "grants": lambda n : setattr(self, 'grants', n.get_collection_of_object_values(PermissionGrantDto)),
            "hasUniquePermissions": lambda n : setattr(self, 'has_unique_permissions', n.get_bool_value()),
            "inheritsFrom": lambda n : setattr(self, 'inherits_from', n.get_str_value()),
            "inheritsFromId": lambda n : setattr(self, 'inherits_from_id', n.get_uuid_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_enum_value("effectiveLevel", self.effective_level)
        writer.write_collection_of_object_values("grants", self.grants)
        writer.write_bool_value("hasUniquePermissions", self.has_unique_permissions)
        writer.write_str_value("inheritsFrom", self.inherits_from)
        writer.write_uuid_value("inheritsFromId", self.inherits_from_id)
        writer.write_additional_data_value(self.additional_data)
    

