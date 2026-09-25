from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class RoleResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The description property
    description: Optional[str] = None
    # The grantsAllScopes property
    grants_all_scopes: Optional[bool] = None
    # The id property
    id: Optional[UUID] = None
    # The isBuiltIn property
    is_built_in: Optional[bool] = None
    # The name property
    name: Optional[str] = None
    # The scopes property
    scopes: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> RoleResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: RoleResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return RoleResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "grantsAllScopes": lambda n : setattr(self, 'grants_all_scopes', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "isBuiltIn": lambda n : setattr(self, 'is_built_in', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "scopes": lambda n : setattr(self, 'scopes', n.get_collection_of_primitive_values(str)),
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
        writer.write_str_value("description", self.description)
        writer.write_bool_value("grantsAllScopes", self.grants_all_scopes)
        writer.write_uuid_value("id", self.id)
        writer.write_bool_value("isBuiltIn", self.is_built_in)
        writer.write_str_value("name", self.name)
        writer.write_collection_of_primitive_values("scopes", self.scopes)
        writer.write_additional_data_value(self.additional_data)
    

