from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class SchemaRule(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The field property
    field: Optional[str] = None
    # The key property
    key: Optional[str] = None
    # The message property
    message: Optional[str] = None
    # The op property
    op: Optional[str] = None
    # The other_field property
    other_field: Optional[str] = None
    # The when_field property
    when_field: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SchemaRule:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SchemaRule
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SchemaRule()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "field": lambda n : setattr(self, 'field', n.get_str_value()),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "message": lambda n : setattr(self, 'message', n.get_str_value()),
            "op": lambda n : setattr(self, 'op', n.get_str_value()),
            "other_field": lambda n : setattr(self, 'other_field', n.get_str_value()),
            "when_field": lambda n : setattr(self, 'when_field', n.get_str_value()),
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
        writer.write_str_value("field", self.field)
        writer.write_str_value("key", self.key)
        writer.write_str_value("message", self.message)
        writer.write_str_value("op", self.op)
        writer.write_str_value("other_field", self.other_field)
        writer.write_str_value("when_field", self.when_field)
        writer.write_additional_data_value(self.additional_data)


