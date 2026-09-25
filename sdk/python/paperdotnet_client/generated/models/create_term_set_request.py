from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class CreateTermSetRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The isOpen property
    is_open: Optional[bool] = False
    # The description property
    description: Optional[str] = None
    # The groupId property
    group_id: Optional[UUID] = None
    # The name property
    name: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CreateTermSetRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CreateTermSetRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CreateTermSetRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "groupId": lambda n : setattr(self, 'group_id', n.get_uuid_value()),
            "isOpen": lambda n : setattr(self, 'is_open', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
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
        writer.write_uuid_value("groupId", self.group_id)
        writer.write_bool_value("isOpen", self.is_open)
        writer.write_str_value("name", self.name)
        writer.write_additional_data_value(self.additional_data)
    

