from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .field_definition_dto import FieldDefinitionDto

@dataclass
class ContentTypeRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The description property
    description: Optional[str] = None
    # The fields property
    fields: Optional[list[FieldDefinitionDto]] = None
    # The name property
    name: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentTypeRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentTypeRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentTypeRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .field_definition_dto import FieldDefinitionDto

        from .field_definition_dto import FieldDefinitionDto

        fields: dict[str, Callable[[Any], None]] = {
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "fields": lambda n : setattr(self, 'fields', n.get_collection_of_object_values(FieldDefinitionDto)),
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
        writer.write_collection_of_object_values("fields", self.fields)
        writer.write_str_value("name", self.name)
        writer.write_additional_data_value(self.additional_data)
    

