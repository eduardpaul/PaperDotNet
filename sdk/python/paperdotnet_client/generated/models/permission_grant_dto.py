from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .principal_type import PrincipalType
    from .workspace_access_level import WorkspaceAccessLevel

@dataclass
class PermissionGrantDto(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # What the current user may do in a workspace. Ordered: higher includes lower.
    level: Optional[WorkspaceAccessLevel] = None
    # The principalId property
    principal_id: Optional[UUID] = None
    # The principalType property
    principal_type: Optional[PrincipalType] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> PermissionGrantDto:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: PermissionGrantDto
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return PermissionGrantDto()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .principal_type import PrincipalType
        from .workspace_access_level import WorkspaceAccessLevel

        from .principal_type import PrincipalType
        from .workspace_access_level import WorkspaceAccessLevel

        fields: dict[str, Callable[[Any], None]] = {
            "level": lambda n : setattr(self, 'level', n.get_enum_value(WorkspaceAccessLevel)),
            "principalId": lambda n : setattr(self, 'principal_id', n.get_uuid_value()),
            "principalType": lambda n : setattr(self, 'principal_type', n.get_enum_value(PrincipalType)),
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
        writer.write_enum_value("level", self.level)
        writer.write_uuid_value("principalId", self.principal_id)
        writer.write_enum_value("principalType", self.principal_type)
        writer.write_additional_data_value(self.additional_data)
    

